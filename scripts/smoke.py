#!/usr/bin/env python3
"""Real local HTTP/storage smoke test. No hardware or external broker; stdlib only."""
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

root = Path(__file__).resolve().parents[1]


def unused_port():
    with socket.socket() as connection:
        connection.bind(('127.0.0.1', 0))
        return connection.getsockname()[1]


with tempfile.TemporaryDirectory(prefix='poltersense-smoke-') as storage:
    port = unused_port()
    base = f'http://127.0.0.1:{port}'
    env = {key: value for key, value in os.environ.items() if not key.startswith('POLTERSENSE_')}
    env.update(POLTERSENSE_CONFIG_DIR=storage, POLTERSENSE_DEMO='true',
               DOTNET_CLI_TELEMETRY_OPTOUT='1')
    logfile = Path(storage) / 'app.log'

    def request(path, method='GET', data=None):
        body = json.dumps(data).encode() if data is not None else None
        req = urllib.request.Request(base + path, data=body, method=method,
                                     headers={'Content-Type': 'application/json'} if body else {})
        try:
            with urllib.request.urlopen(req, timeout=5) as response:
                content = response.read().decode()
                return response.status, json.loads(content) if content.startswith('{') else content
        except urllib.error.HTTPError as error:
            return error.code, error.read().decode()

    def wait(predicate, seconds=20):
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            try:
                if predicate():
                    return
            except (OSError, urllib.error.URLError):
                pass
            time.sleep(0.1)
        raise AssertionError('Local smoke condition timed out')

    def start():
        handle = logfile.open('ab')
        process = subprocess.Popen(['dotnet', 'run', '--no-build', '--project', 'src',
                                    '--no-launch-profile', '--urls', base], cwd=root, env=env,
                                   stdout=handle, stderr=subprocess.STDOUT)
        handle.close()
        wait(lambda: request('/healthz') == (200, 'Healthy'))
        return process

    def stop(process):
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)

    app = start()
    try:
        assert request('/')[0] == 200, 'Build frontend before running this smoke test'
        initial = request('/api/config')[1]
        assert initial['config']['wisps'] == [] and initial['config']['floors'] == []
        assert request('/api/state')[1]['mqtt']['status'] == 'unconfigured'
        for path in ['/api/device', '/api/devices', '/api/nodes', '/api/state/devices',
                     '/api/state/calibration', '/api/firmware/types', '/api/mcp', '/api/history']:
            assert request(path)[0] == 404, path
        code, view = request('/api/config/yaml', 'PUT', {'revision': initial['revision'], 'yaml': (root / 'src/config.example.yaml').read_text()})
        assert code == 200 and len(view['config']['floors']) == 2
        previous = view['revision']
        view['config']['wisps'][0]['name'] = 'Smoke Wisp'
        view['config']['wisps'][0]['point'][0] = 1.75
        view['config']['sources'][0]['name'] = 'Smoke Source'
        view['config']['links'][0]['sourceId'] = view['config']['sources'][1]['id']
        code, updated = request('/api/config', 'PUT', {'revision': previous, 'config': view['config']})
        assert code == 200 and updated['revision'] != previous
        assert request('/api/config', 'PUT', {'revision': previous, 'config': view['config']})[0] == 409
        config_path = Path(storage) / 'config.yaml'
        accepted_yaml = config_path.read_text()
        assert 'Smoke Wisp' in accepted_yaml
        code, _ = request('/api/config/yaml', 'PUT', {'revision': updated['revision'], 'yaml': 'password: secret-error-sentinel\nfloors: ['})
        assert code == 400 and config_path.read_text() == accepted_yaml
        config_path.write_text('password: secret-error-sentinel\nfloors: [')
        wait(lambda: request('/api/config')[1]['error'] is not None)
        assert request('/api/config')[1]['revision'] == updated['revision']
        assert request('/api/config', 'PUT', {'revision': updated['revision'], 'config': view['config']})[0] == 409
        config_path.write_text(accepted_yaml.replace('Smoke Wisp', 'Externally edited Wisp'))
        wait(lambda: request('/api/config')[1]['config']['wisps'][0]['name'] == 'Externally edited Wisp')
        replay_code, replay = request('/api/demo/replay', 'POST')
        assert replay_code == 200 and replay['accepted'] == 3 and replay['rejected'] == 0
        current = request('/api/config')[1]
        assert replay['configRevision'] != current['revision']
        state = request('/api/state')[1]
        assert state['medium']['availability'] == 'notImplemented'
        assert state['medium']['tracks'] is None and all(room['state'] == 'unknown' for room in state['medium']['rooms'])
        assert state['medium']['inputHealth'] == 'noInput'
        assert state['diagnostics']['latest'][0]['provenance']['origin'] == 'replay'
        assert 'secret-error-sentinel' not in json.dumps(current) + json.dumps(state)
        identity = (Path(storage) / 'instance-id').read_text()
        stop(app)
        # Unreachable broker cannot prevent startup; credentials remain private even on connection errors.
        env.update(POLTERSENSE_MQTT_HOST='127.0.0.1', POLTERSENSE_MQTT_PORT=str(unused_port()),
                   POLTERSENSE_MQTT_USERNAME='username-sentinel', POLTERSENSE_MQTT_PASSWORD='password-sentinel')
        env['POLTERSENSE_DEMO'] = 'false'
        app = start()
        wait(lambda: request('/api/state')[1]['mqtt']['status'] == 'disconnected')
        persisted = request('/api/config')[1]
        assert persisted['revision'] == current['revision']
        assert (Path(storage) / 'instance-id').read_text() == identity
        assert request('/api/demo/replay', 'POST')[0] == 404
        assert request('/healthz') == (200, 'Healthy')
        public = json.dumps(persisted) + json.dumps(request('/api/state')[1])
        logs = logfile.read_text()
        for marker in ['username-sentinel', 'password-sentinel', 'secret-error-sentinel']:
            assert marker not in public + logs
        print('PASS: empty/no broker startup, UI/API/health, removed endpoints, YAML and inventory persistence, conflicts, invalid/live edits, recorded replay, restart, unreachable broker, secret-safe public state/logging')
    finally:
        stop(app)
