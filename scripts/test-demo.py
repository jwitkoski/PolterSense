#!/usr/bin/env python3
"""Exercise the default Docker demo with host build tools absent from PATH."""
import json
import argparse
import ipaddress
import os
from pathlib import Path
import re
import shutil
import signal
import socket
import subprocess
import tempfile
import time
import urllib.request

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--bind', type=ipaddress.IPv4Address, help='Test an explicit local IPv4 binding')
args = parser.parse_args()
bind = str(args.bind) if args.bind else '127.0.0.1'
request_host = '127.0.0.1' if bind == '0.0.0.0' else bind
docker = shutil.which('docker')
if not docker:
    raise SystemExit('This integration test requires Docker.')
subprocess.run([docker, 'info'], check=True, stdout=subprocess.DEVNULL)

with tempfile.TemporaryDirectory(prefix='poltersense-demo-test-') as temporary:
    directory = Path(temporary)
    binaries = directory / 'bin'
    binaries.mkdir()
    for name in ['bash', 'dirname', 'docker']:
        (binaries / name).symlink_to(shutil.which(name))
    with socket.socket() as connection:
        connection.bind((bind, 0))
        port = connection.getsockname()[1]
    env = {key: value for key, value in os.environ.items() if not key.startswith('POLTERSENSE_')}
    env.update(PATH=str(binaries), POLTERSENSE_DEMO_PORT=str(port))
    if args.bind:
        env['POLTERSENSE_DEMO_BIND'] = bind
    log = directory / 'demo.log'
    container = None
    volume = None

    def request(path, method='GET'):
        req = urllib.request.Request(f'http://{request_host}:{port}{path}', method=method)
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(req, timeout=5) as response:
            text = response.read().decode()
            return json.loads(text) if text.startswith('{') else text

    with log.open('w') as output:
        process = subprocess.Popen(['./scripts/demo.sh'], cwd=root, env=env,
                                   stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
    try:
        deadline = time.monotonic() + 600
        while time.monotonic() < deadline:
            content = log.read_text()
            match = re.search(r'^Demo container: ([a-f0-9]{64})$', content, re.MULTILINE)
            if match:
                container = match[1]
            if container and 'Now listening on:' in content:
                break
            if process.poll() is not None:
                raise AssertionError('Demo exited before startup')
            time.sleep(0.2)
        else:
            raise AssertionError('Demo startup timed out')

        details = json.loads(subprocess.check_output([docker, 'inspect', container]))[0]
        mounts = details['Mounts']
        assert len(mounts) == 1 and mounts[0]['Type'] == 'volume'
        assert mounts[0]['Destination'] == '/config/poltersense'
        volume = mounts[0]['Name']
        assert details['Config']['User'] == 'app'
        bindings = details['NetworkSettings']['Ports']['5279/tcp']
        assert len(bindings) == 1 and bindings[0]['HostIp'] == bind
        if bind != '0.0.0.0':
            assert f'Open http://{bind}:{port}' in content
        assert request('/healthz') == 'Healthy'
        assert '<html' in request('/')
        scene = request('/api/config')['config']
        assert len(scene['floors']) == 2 and scene['wisps'] and scene['sources'] and scene['links']
        assert request('/api/state')['mqtt']['status'] == 'unconfigured'
        replay = request('/api/demo/replay', 'POST')
        assert replay['accepted'] == 3 and replay['rejected'] == 0
        medium = request('/api/state')['medium']
        assert medium['availability'] == 'notImplemented' and medium['tracks'] is None
        assert all(room['state'] == 'unknown' for room in medium['rooms'])

        os.killpg(process.pid, signal.SIGINT)
        process.wait(timeout=30)
        # A reachable engine is required before an inspect failure means absence.
        subprocess.run([docker, 'info'], check=True, stdout=subprocess.DEVNULL)
        for kind, identifier in [('container', container), ('volume', volume)]:
            result = subprocess.run([docker, kind, 'inspect', identifier], capture_output=True)
            assert result.returncode != 0, f'Demo {kind} was not removed on Ctrl+C'
        print(f'PASS: demo without host SDKs; health/UI/scene/replay at {request_host}; binding {bind}; Ctrl+C container/volume cleanup')
    except BaseException:
        print(log.read_text())
        raise
    finally:
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=5)
        if container:
            subprocess.run([docker, 'rm', '--force', '--volumes', container], capture_output=True)
