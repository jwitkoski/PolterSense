#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

mode=${1:---docker}
case "$mode" in
  --help|-h)
    echo 'Usage: ./scripts/demo.sh [--docker|--native]'
    echo 'Default: Docker-only demo; no host .NET, Node or pnpm required.'
    echo 'Native: requires .NET SDK 8, Node.js 22 and pnpm >=10.26.'
    echo 'POLTERSENSE_DEMO_PORT optionally changes the port (default 5279).'
    echo 'POLTERSENSE_DEMO_BIND selects a local IPv4 address (default 127.0.0.1).'
    echo 'For phone access, set it to the local LAN IP and open the printed HTTP URL.'
    exit 0
    ;;
  --docker|--native) ;;
  *) echo "Unknown option: $mode. Use --help." >&2; exit 2 ;;
esac
if (( $# > 1 )); then echo 'Expected at most one option. Use --help.' >&2; exit 2; fi
port=${POLTERSENSE_DEMO_PORT:-5279}
if [[ ! "$port" =~ ^[0-9]{1,5}$ ]] || (( 10#$port < 1 || 10#$port > 65535 )); then
  echo 'POLTERSENSE_DEMO_PORT must be an integer from 1 to 65535.' >&2
  exit 2
fi
port=$((10#$port))
bind=${POLTERSENSE_DEMO_BIND:-127.0.0.1}
if [[ ! "$bind" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]]; then
  echo 'POLTERSENSE_DEMO_BIND must be a local IPv4 address.' >&2
  exit 2
fi
IFS=. read -r -a octets <<< "$bind"
for octet in "${octets[@]}"; do
  if (( 10#$octet > 255 )) || [[ "$octet" != "$((10#$octet))" ]]; then
    echo 'POLTERSENSE_DEMO_BIND must be a local IPv4 address without leading zeros.' >&2
    exit 2
  fi
done
show_url() {
  if [[ "$bind" == 127.* ]]; then
    echo 'Local-only demo: phones cannot connect. Set POLTERSENSE_DEMO_BIND to the local LAN IP for phone access.'
  else
    echo 'Network demo: anyone who can reach this address can view and edit the fictional configuration (no authentication).'
  fi
  if [[ "$bind" == 0.0.0.0 ]]; then
    echo "Open http://<this-machine-LAN-IP>:$port and select Diagnostics → Replay synthetic Echo."
  else
    echo "Open http://$bind:$port and select Diagnostics → Replay synthetic Echo."
  fi
}

if [[ "$mode" == --docker ]]; then
  if ! command -v docker >/dev/null 2>&1 || ! docker info >/dev/null 2>&1; then
    echo 'The demo requires access to a running Docker engine. Native development is available with --native; see BUILDING.md.' >&2
    exit 1
  fi
  echo 'Building the isolated demo with Docker; no host .NET, Node or pnpm is needed.'
  docker build --tag poltersense:demo .
  container_id=''
  cleanup() {
    trap '' INT TERM
    if [[ -n "$container_id" ]]; then
      # Only the container successfully created by this invocation and its anonymous volume.
      docker stop --time 5 "$container_id" >/dev/null
      docker rm --volumes "$container_id" >/dev/null
    fi
  }
  trap cleanup EXIT
  trap 'exit 130' INT
  trap 'exit 143' TERM
  container_id=$(docker create --init --publish "$bind:$port:5279" \
    --env POLTERSENSE_DEMO=true poltersense:demo)
  # Seed only this new container's anonymous volume. No existing configuration or
  # host MQTT environment is mounted/forwarded into the demo.
  docker cp src/config.example.yaml "$container_id:/config/poltersense/config.yaml"
  echo "Demo container: $container_id"
  show_url
  echo 'Ctrl+C stops the demo and removes its temporary configuration. The local image is kept.'
  docker start --attach "$container_id"
  exit
fi

# Native development is an explicit choice, with all tools checked before writes.
for tool in node pnpm dotnet; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "Missing $tool. Native mode requires Node.js 22, pnpm >=10.26 and .NET SDK 8. Run ./scripts/demo.sh for the Docker-only demo." >&2
    exit 1
  fi
done
demo_dir=$(mktemp -d "${TMPDIR:-/tmp}/poltersense-demo.XXXXXX")
cp src/config.example.yaml "$demo_dir/config.yaml"
echo "Fictional demo storage: $demo_dir"
pnpm -C src/ui install --frozen-lockfile
pnpm -C src/ui build
show_url
env -u POLTERSENSE_MQTT_HOST -u POLTERSENSE_MQTT_USERNAME -u POLTERSENSE_MQTT_PASSWORD -u POLTERSENSE_TEST_INPUT \
  POLTERSENSE_CONFIG_DIR="$demo_dir" POLTERSENSE_DEMO=true \
  dotnet run --project src --no-launch-profile --urls "http://$bind:$port"
