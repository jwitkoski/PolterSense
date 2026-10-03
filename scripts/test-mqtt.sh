#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
broker_dir=$(mktemp -d "${TMPDIR:-/tmp}/poltersense-broker.XXXXXX")
broker_name="poltersense-test-$(basename "$broker_dir" | tr '[:upper:]' '[:lower:]')"
cleanup() { docker rm -f "$broker_name" >/dev/null 2>&1 || true; rm -rf "$broker_dir"; }
trap cleanup EXIT
printf 'listener 1883\nallow_anonymous true\npersistence false\n' > "$broker_dir/mosquitto.conf"
chmod 755 "$broker_dir"
chmod 644 "$broker_dir/mosquitto.conf"
docker run --rm -d --name "$broker_name" -p 127.0.0.1::1883 \
  --mount "type=bind,source=$broker_dir/mosquitto.conf,target=/mosquitto/config/mosquitto.conf,readonly" eclipse-mosquitto:2 >/dev/null
broker_port=$(docker port "$broker_name" 1883/tcp | cut -d: -f2)
POLTERSENSE_TEST_BROKER_PORT="$broker_port" dotnet test --filter 'Category=MqttIntegration'
