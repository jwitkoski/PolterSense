# Building PolterSense

For the demo, run `./scripts/demo.sh` with Docker; no host SDKs or package managers are required.

`python3 scripts/test-demo.py` validates that exact default launcher with only Bash, dirname and Docker on PATH. It checks startup, health, UI serving, fictional inventory, replay and Ctrl+C cleanup using its own temporary container/volume. CI runs this independently of native toolchain setup. Add `--bind <local-IPv4>` to exercise an explicit interface binding; this temporarily exposes the fictional demo on that address and checks HTTP through it.

For native development and the checks below, use .NET SDK 8, Node.js 22, and pnpm 10.26 or newer (tested with 10.34.6 and 12.8.1). Run these commands from the repository root. No broker, receiver hardware, ESPresense installation, or external service is needed for the default application.

```sh
pnpm -C src/ui install --frozen-lockfile
dotnet build
dotnet test
pnpm -C src/ui check
pnpm -C src/ui lint
pnpm -C src/ui test:unit --run
pnpm -C src/ui build
pnpm -C src/ui exec playwright install chromium
pnpm -C src/ui test
python3 scripts/smoke.py
./scripts/test-mqtt.sh
```

The browser tests may require operating-system browser libraries; in a disposable development container or CI runner, `pnpm -C src/ui exec playwright install --with-deps chromium` installs them. The read-only GitHub validation workflow runs these checks and does not publish artifacts, images, packages, or releases.

The repository's `src/ui/pnpm-workspace.yaml` approves only esbuild's dependency install script via `allowBuilds`. This policy must be present before installation, including in container builds. Do not replace it with a blanket build-script allowance. Older `onlyBuiltDependencies` settings are not recognized by pnpm 11+.

## Development

```sh
# Terminal 1: API and summarized WebSocket state, http://127.0.0.1:5279
POLTERSENSE_CONFIG_DIR="$PWD/.poltersense" dotnet watch --project src/PolterSense.csproj
# Terminal 2: UI, http://localhost:5173; Vite proxies /api and /ws to the backend
pnpm -C src/ui start
```

The application creates an empty scene in its dedicated storage directory. To try the fictional map, deliberately copy `src/config.example.yaml` to that directory as `config.yaml`. Never point PolterSense at Companion storage. See [README](README.md) for configuration, transport environment settings, and security boundaries.

## Isolated synthetic demonstration

Run `./scripts/demo.sh` for the Docker-only fictional scene and synthetic Echo replay at http://127.0.0.1:5279. For phone access, set `POLTERSENSE_DEMO_BIND` to the machine's LAN IPv4 address and use the printed HTTP URL. The default binds only to 127.0.0.1. LAN clients can edit the fictional scene without authentication; use a trusted network. It builds locally, uses a new anonymous volume, forwards no host MQTT configuration, and removes its container/volume on Ctrl+C. The image remains local. An occupied port can be changed with `POLTERSENSE_DEMO_PORT=5280`. `./scripts/demo.sh --native` is the explicit host-toolchain alternative; it requires all three tools listed above and retains its printed temporary configuration directory. Run `scripts/test-mqtt.sh` for the disposable-broker integration check. Both are repository-local test paths; neither establishes ESPectre compatibility or localization performance. See [protocol notes](docs/protocols.md) for their observation contract.

## Local publish and container

Publishing builds the frontend and includes it in the ASP.NET application's `wwwroot`:

```sh
dotnet publish src/PolterSense.csproj --configuration Release --output /tmp/poltersense-publish
(
  cd /tmp/poltersense-publish
  POLTERSENSE_CONFIG_DIR=/tmp/poltersense-local ASPNETCORE_URLS=http://127.0.0.1:5279 \
    dotnet PolterSense.dll
)
```

The working directory selects the published static files and bundled fixtures.

```sh
docker build --tag poltersense:local .
docker run --rm --name poltersense-local -p 127.0.0.1:5279:5279 \
  --mount type=volume,source=poltersense-local,target=/config/poltersense poltersense:local
```

The runtime image contains ASP.NET 8 and application output, runs as the standard `app` user (UID 1654), and stores configuration at `/config/poltersense`. A host bind mount must be writable by that user. No MQTT connection is configured by default. `/healthz`, `/api/config`, `/api/state`, and the UI remain available without a broker. These commands build and run locally; they do not push an image or deploy to a server.
