# Foundation validation — 2026-10-03

Starting commit `cf545051046b4201ec91bb43700c6086030808eb`; initially clean. Changes are local/uncommitted on `main` at the owner's explicit direction. No push, PR, publication, production connection, server deployment or hardware changes.

## Toolchain and baseline

Host initially had Node18.19.1 and Docker, but no dotnet/pnpm. Installed isolated .NET SDK8.0.425, Node22 and pnpm10.34.6 under `/tmp/poltersense-tools`; no global service/tool installation. Backend baseline: `dotnet test` passed220, skipped2 (inherited ignored tests). Untouched frontend baseline (archive of starting commit with GIT_DIR pointing at original checkout): check0 errors/11warnings, build passed, unit19 passed, lint passed with obsolete `--plugin-search-dir` warning. Initial archive checks could not resolve git version metadata; supplying GIT_DIR fixed the harness without source edits. No inherited failing relevant tests were hidden; BLE tests were removed with their deleted behavior. Map/coordinate regressions were retained/adapted.

For this machine's temporary toolchain:

```sh
export PATH=/tmp/poltersense-tools/node/bin:/tmp/poltersense-tools/pnpm/node_modules/.bin:/tmp/poltersense-tools/dotnet:$PATH
export DOTNET_CLI_HOME=/tmp/poltersense-tools/dotnet-home
export NUGET_PACKAGES=/tmp/poltersense-tools/nuget
# Only for the isolated pnpm store used during this task:
export npm_config_store_dir=/tmp/poltersense-tools/pnpm-store
```

Normal installed SDK/Node/pnpm setups need none of these paths. Primary logs are `/tmp/poltersense-tools/{baseline-*,final-*,ui-*,docker-build,container-browser}.log`; no credentials were logged.

## Commands and outcomes

| Command | Result |
|---|---|
| `dotnet build` | Pass, 0 warnings/errors |
| `dotnet test` | 58 passed; 1 broker test explicitly skipped pending its harness |
| `./scripts/test-mqtt.sh` | 1 real Mosquitto integration test passed; disposable container removed |
| `pnpm -C src/ui install --frozen-lockfile` (isolated store) | Pass; pruned unused dependencies, no retained dependency upgrades |
| `pnpm -C src/ui check` | 0 errors, 0 warnings |
| `pnpm -C src/ui lint` | Prettier and ESLint pass |
| `pnpm -C src/ui test:unit` | 6 passed |
| `pnpm -C src/ui build` | Pass, static `build/index.html` |
| `POLTERSENSE_CHROME=/opt/google/chrome/chrome pnpm -C src/ui test` | 3 mocked browser tests passed |
| `POLTERSENSE_E2E_URL=http://127.0.0.1:15280 POLTERSENSE_CHROME=/opt/google/chrome/chrome pnpm -C src/ui test` | 3 real-backend browser tests passed |
| `python3 scripts/smoke.py` | Real HTTP/storage/startup/restart checks passed |
| `DOCKER_CONFIG=/tmp/poltersense-tools/docker docker build -t poltersense:foundation-test .` | Pass; local image only |
| `docker run --rm -d --name poltersense-foundation-smoke -p 127.0.0.1::5279 -e POLTERSENSE_DEMO=true poltersense:foundation-test` | Starts as user `app`, isolated empty storage, no broker |
| Container `/healthz`, `/api/config`, `/api/state`, static asset GET; example YAML PUT; replay POST | All pass; bundled fixture accepted3; inference unknown, tracks null |
| `POLTERSENSE_E2E_URL=http://127.0.0.1:32771 POLTERSENSE_CHROME=/opt/google/chrome/chrome pnpm -C src/ui test` | 3 browser tests passed against built container (29.2s); example restored afterward |
| `git diff --check` | Clean |

Browser Chrome needed execution outside the filesystem/network sandbox to reach the isolated loopback test servers; sandboxed Chrome returned `ERR_ACCESS_DENIED`. Docker buildx's default activity directory was read-only, resolved with a fresh temporary DOCKER_CONFIG (no credentials copied). These environmental issues were resolved; no validation gate remains blocked. A container HTML-shell assertion initially expected client-rendered title text before JavaScript; corrected check fetched actual static assets, then the full browser test validated rendering.

## What the checks establish

- Empty configuration/no broker/no estimator starts and serves UI, API and health; unreachable broker retries without blocking. Dedicated storage/client identity survives restart.
- Fictional floors/rooms and radio inventories render. Wisp/Source metadata and coordinates and link assignments persist; another browser refreshes without reload. Invalid YAML preserves drafts and last valid map. Revision/disk-edit conflicts reject; external valid edits reload and invalid edits remain untouched. Restart restores valid state.
- Only ConfigLoader, PipelineHost and MqttCoordinator are registered background services. BLE/MCP/firmware/history/tracker/optimizer implementations and routes were removed; HTTP smoke checks old routes return404. Runtime namespaces/dependencies/build paths are PolterSense; LICENSE unchanged.
- Typed synthetic data reaches the adapter, bounded queue and no-op Medium. Tests reject malformed/unknown/duplicate JSON keys, versions, invalid references/revisions/payloads/timing, retained/stale/out-of-order/duplicate evidence. Identity ordering only applies when session and sequence support it; queue overflow/cancellation are explicit.
- Acquired evidence ages independently of arrival. Unknown acquisition/firmware/radio fields stay unknown. No-op returns unknown rooms and null tracks. Repeated Echo replay uses its hashed recorded scene with deterministic processing. Replay cannot overwrite live summaries or current input health for the same Wisp.
- Real MQTT5 broker test exercises connect/reconnect through TCP interruption, Last Will, nonretained input, retained rejection on delivery/reconnect, and an unchanged foreign retained sentinel. All gateway publications stay under its PolterSense status topic. A protocol-level SUBACK rejection test proves failed setup cannot report connected and retries.
- Public snapshots, responses and captured error logs exclude test username/password and invalid YAML secret sentinels. No operational HA publication, firmware command, BLE discovery or retained clearing exists.

## Limits

No hardware was tested and no real ESPectre adapter is implemented. No occupancy/location/pet/person accuracy claim follows from these tests. Raw CSI and firmware management remain deferred. TLS keeps certificate validation enabled but a real TLS broker was not separately exercised. Configuration form saves reserialize YAML/comments; YAML text saves preserve comments. Arbitrary external editors do not participate in a cross-process lock, so there remains a narrow external-editor race at final rename despite serialized GUI writes and two disk-content checks. Diagnostic/order caches are bounded process-local state; their exact limits and restart behavior are in protocols.md. Background images are temporary local overlays, not spatially calibrated persisted geometry.

## Demo install correction — 2026-10-03

The first handoff did **not** execute `scripts/demo.sh` end to end; its constituent build/start/API paths were tested with pnpm10.34.6. The owner then hit `ERR_PNPM_IGNORED_BUILDS` with pnpm12.8.1. Root cause: inherited `onlyBuiltDependencies: [esbuild]` is no longer recognized by pnpm11+, which generated an undecided `allowBuilds.esbuild` placeholder. This was a package-manager-version validation gap, not an application test failure or a need to permit all dependency scripts.

Replaced the legacy setting with `allowBuilds: {esbuild: true}`, after inspecting the locked esbuild0.27.7 install script (select/install its platform binary and verify its version). Docker now copies the workspace policy before `pnpm install`. Demo checks required commands before creating data/installing dependencies. BUILDING.md records pnpm>=10.26 support; CI adds a fresh-store install/build with12.8.1 alongside its existing pnpm10 job. No global configuration or user's installed modules/store was modified.

Verification performed:

- In a temporary copy with pnpm12.8.1, the original workspace settings reproduced the exact ignored-esbuild error. Log: `/tmp/poltersense-tools/demo-install-before.log`.
- Ran the **actual `./scripts/demo.sh`** in a second fresh temporary copy, empty node_modules and empty pnpm store, with Node22, pnpm12.8.1 and .NET8 on PATH. esbuild's postinstall executed successfully. Frontend build, backend start, `/healthz`, UI HTML, fictional two-floor configuration and replay (`accepted: 3`, no tracks) all passed. The temporary server was stopped. Log: `/tmp/poltersense-tools/demo-script-fixed.log`; harness: `/tmp/poltersense-tools/check-demo-script.py`.
- `DOCKER_CONFIG=/tmp/poltersense-tools/docker docker build -t poltersense:demo-policy-test .` passed with pnpm10 and the copied approval policy; esbuild postinstall ran. Log: `/tmp/poltersense-tools/demo-policy-docker.log`.
- `bash -n scripts/demo.sh`, workflow YAML parse and `git diff --check` passed. CI definition was updated locally, not run remotely.

Fresh builds emitted a nonfatal warning before SvelteKit generated `.svelte-kit/tsconfig.json`; both builds completed. Reference: [pnpm build settings](https://pnpm.io/settings/build#allowbuilds) documents `allowBuilds` and removal of the legacy setting.

## Docker-only demo correction — 2026-10-03

The native demo verification above used a temporary .NET SDK on PATH. The owner's shell lacked `dotnet`; handing back that command as ready to run was incorrect. The default `./scripts/demo.sh` now uses the already available Docker engine, requiring no host .NET, Node or pnpm. `--native` explicitly selects the developer toolchain. It builds a local image, binds only loopback, seeds a fresh anonymous volume from the fictional example, and forwards no host MQTT settings. It never mounts existing configuration.

Validation performed:

- `PATH=/usr/bin:/bin ./scripts/demo.sh` built and started successfully without host dotnet/pnpm. Health, UI HTML, two-floor inventory, unconfigured MQTT and synthetic replay (`accepted: 3`, `rejected: 0`, tracks null) passed. Log: `/tmp/poltersense-demo-docker-only.log`.
- `POLTERSENSE_E2E_URL=http://127.0.0.1:5279 POLTERSENSE_CHROME=/opt/google/chrome/chrome /tmp/poltersense-tools/node/bin/node node_modules/@playwright/test/cli.js test` (from src/ui) passed all three browser tests against that actual demo, including rendered geometry and persisted inventory/coordinate edits. The browser harness used temporary Node; the demo launcher did not. Log: `/tmp/poltersense-demo-docker-browser.log`.
- The first Ctrl+C test exposed a Docker force-removal failure during process exit, leaving a stopped test container. The launcher now uses container init and graceful stop before removal, and reports cleanup errors. The leftover test container/volume was removed explicitly.
- `python3 scripts/test-demo.py` passed with a PATH containing **only Bash, dirname and Docker**. It exercises the actual default script, checks health/UI/fictional inventory/replay, verifies non-root runtime and loopback-only port, sends Ctrl+C, waits for script exit and verifies both its container and anonymous volume are absent. Log: `/tmp/poltersense-test-demo.log`. CI now contains this same check with no host SDK setup; the remote workflow has not been run.

Docker build and browser execution required the same sandbox exceptions described above. No system SDKs, global package-manager policy, production services or hardware were changed. The local demo image is retained; its test data is temporary.

## Phone-access binding correction — 2026-10-03

The default loopback binding cannot serve a phone connecting to the host LAN address. Added validated `POLTERSENSE_DEMO_BIND` for an explicit IPv4 interface in Docker/native launch paths; default remains 127.0.0.1. Startup/help and README/BUILDING now distinguish local-only and LAN access and print the selected HTTP URL. LAN mode exposes the unauthenticated fictional scene to reachable clients; no firewall/router changes were made.

- `python3 scripts/test-demo.py` passed: default loopback binding, health/UI/inventory/replay and cleanup, with no host build tools on PATH.
- `python3 scripts/test-demo.py --bind 10.69.42.69` passed against this host's actual LAN address on an ephemeral port: correct Docker binding, HTTP UI/API/health and replay, followed by removal of the test container/volume. Logs: `/tmp/poltersense-demo-bind-{default,lan}.log`.
- Invalid address/port suffix/out-of-range octet/leading-zero cases returned exit2 before building. Bash syntax and `git diff --check` passed.

The LAN test connected from the host to its LAN address; it does not establish reachability from the owner's phone or across network VLANs. Existing user demo containers were not stopped or modified. Restart the running script with the explicit binding to apply it.
