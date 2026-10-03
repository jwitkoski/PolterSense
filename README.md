# PolterSense

A local Wi-Fi sensing application foundation, derived from [ESPresense Companion](https://github.com/ESPresense/ESPresense-companion). ASP.NET Core and Svelte retain the useful map, configuration, UI and transport infrastructure while replacing the BLE measurement model.

**Implemented now:** multifloor geometry, rooms, Wisp receiver and Source radio inventory, sensing-link assignments, validated live YAML/GUI configuration, REST/WebSocket summaries, isolated optional MQTT, typed observation admission, synthetic Echo replay and an explicitly unimplemented Medium, plus ESPectre discovery and capability-checked receiver settings.

**Future intent:** validated motion/occupancy estimates, spatial likelihoods, anonymous entity tracks across rooms/floors, potentially pet/person distinctions, and deliberate experiment/calibration workflows. None of these sensing capabilities is implemented or established by the synthetic demo. Unknown entity class remains valid. An anonymous track is not a person's identity.

## Run the isolated demonstration

Requires only access to a running Docker engine. No host .NET, Node.js or pnpm installation, broker, or hardware is needed.

```sh
./scripts/demo.sh
# Open http://127.0.0.1:5279
```

That address works only on the machine running the script. For access from a phone, stop the demo with Ctrl+C and restart bound to the machine's LAN IPv4 address, for example:

```sh
POLTERSENSE_DEMO_BIND=192.168.1.50 ./scripts/demo.sh
# On the phone, open http://192.168.1.50:5279 (replace with your machine's IP).
```

The LAN demo has no authentication: clients able to reach it can edit its fictional configuration. Use a trusted network. The script does not change firewall or router rules.

The script builds a local image and starts a container (loopback-only by default) with a **fictional** two-floor house in a fresh anonymous volume. No host configuration or MQTT settings are mounted or forwarded. Ctrl+C removes the demo container and its temporary data; the local image is kept. Use `POLTERSENSE_DEMO_PORT=5280 ./scripts/demo.sh` if port 5279 is occupied. Choose **Diagnostics → Replay synthetic Echo**. Three synthetic observations pass through the adapter, admission pipeline and no-op Medium; diagnostics update through the summarized WebSocket feed. Occupancy stays unknown and tracks remain `null`. Repeating replay is deterministic; the Echo uses its recorded scene even after you edit current geometry.

Use the inventory forms to edit Wisp/Source metadata and absolute coordinates or assign links. Use YAML for floors, room polygons and optional orientation. The map retains floor selection, zoom/pan, coordinate clipboard tools, room colors and local background images. Backgrounds are browser-local reference overlays, not persisted/calibrated geometry. Shift+wheel zooms; focus the map for arrow-key pan, +/− zoom and 0 reset.

For native development, install .NET SDK 8, Node.js 22 and pnpm >=10.26, then use `./scripts/demo.sh --native` for the fictional demo. For a clean empty native installation, build the frontend and run the backend:

```sh
pnpm -C src/ui install --frozen-lockfile
pnpm -C src/ui build
dotnet run --project src
```

Default storage is `~/.poltersense`; override with `POLTERSENSE_CONFIG_DIR`. A new installation has zero Wisps, no MQTT broker and no estimator. Health is `/healthz`, scene is `/api/config`, summarized diagnostics are `/api/state`, and WebSocket summaries are `/ws`. The backend serves the built UI; separate Vite development is documented in [BUILDING.md](BUILDING.md).

## Discover and configure an ESPectre Wisp

Open **Wisp setup**. It performs one read-only discovery pass using ESPectre's mDNS bootstrap and saved receiver endpoints. **Discover receivers** repeats it. Docker bridge networks and VLANs may block multicast: enter a known receiver IPv4 address and leave the Direct HTTP port at **62587**, then select **Discover by address**. The receiver browses its own subnet and PolterSense verifies each candidate's identity and capabilities over HTTP. No subnet sweep or MQTT broker is needed.

Select a candidate, then map it to an existing Wisp or create one with an explicit display name, stable ID and placement. Discovery cannot determine coordinates. This saves `external_id` and `direct_http: {address, port}` through the normal YAML configuration service. Local Wisp names stay independent of firmware labels. Rediscovery can update a changed address without changing Wisp identity or geometry.

For a mapped receiver, **Read** / **Refresh device** loads current firmware and sensing state. Separate **Apply label to device** and **Apply sensing to device** buttons send real settings only after an explicit action. Controls require advertised support; the server rechecks capabilities and identity, validates fields, and requires matching readback before confirming success. A timeout can leave a change applied but unconfirmed: refresh before retrying. These operations are available in demo mode only through the same explicit user actions; synthetic replay itself never touches hardware.

Supported settings: firmware label, sensing enabled, detector, threshold, motion-on/off hit counts and traffic mode. This is a management connection, **not yet live observation ingestion**. OTA, reboot, raw CSI, Wi-Fi/AP changes, MQTT credentials and recalibration controls remain outside this implementation. Endpoints are restricted to RFC1918 IPv4 addresses; Direct HTTP is unauthenticated and intended for a trusted LAN. No firmware or router modification is needed.

## Geometry and configuration

Scene schema 1 uses one building-wide Cartesian frame in **meters**, with absolute x/y/z. Floor bounds are `[[minX,minY,minZ],[maxX,maxY,maxZ]]`; elevation is absolute z; room polygons retain `points: [[x,y], ...]`. A Wisp 1.5 m above a floor at z=3 has z=4.5. IDs are explicit and independent of display names. Floor membership is metadata, not radio coverage or the location of observed motion. Optional orientation is `[yaw,pitch,roll]` in degrees; no propagation simulation uses it.

A Source represents a transmitting **radio**, with optional physical AP label, configured radio identity, BSSID/channel and placement. It is not an SSID or a traffic generator's IP destination. Wisps have a separate optional external firmware identity. Missing metadata remains unknown.

`config.yaml` is the sole scene authority. GUI and YAML edits use one validation/persistence service, revision preconditions and atomic replacement. Invalid changes retain the last valid scene; `config.last-valid.yaml` supports restart recovery. External edits reload within approximately one second. If an external edit is invalid, fix that file before using GUI saves. YAML editor saves retain comments; inventory form saves serialize the complete typed scene and therefore reformat YAML/remove comments. Coordinate/ref/size validation applies equally to both.

No Companion configuration is loaded or migrated. To copy geometry deliberately, copy selected `floors`/room `points` and supported `map` settings into a new PolterSense file; add explicit room/floor IDs and elevations, then validate through the YAML editor. Create Wisp/Source/link records separately after reviewing their actual meaning. Do not copy BLE, MQTT, tracker or optimizer sections. [src/config.example.yaml](src/config.example.yaml) is fictional, not the owner's house.

## Optional MQTT test transport

MQTT is disabled unless `POLTERSENSE_MQTT_HOST` is set. Optional settings: `POLTERSENSE_MQTT_PORT` (default1883), `POLTERSENSE_MQTT_TLS=true`, `POLTERSENSE_MQTT_USERNAME`, `POLTERSENSE_MQTT_PASSWORD`. Credentials are environment-only and never enter public scene snapshots/revisions. TLS uses normal certificate/hostname verification. Failed connections retry without preventing map/API use.

Each storage directory persists a random `instance-id`; client ID is `poltersense-<instance-id>`, status/Last Will is `poltersense/<instance-id>/status`. There are no device commands, legacy topics, retained deletion or Home Assistant entity publications. Real observation adapters are not implemented; the separate Direct HTTP management adapter does not subscribe to motion.

`POLTERSENSE_TEST_INPUT=true` explicitly enables **our normalized synthetic test protocol** at `poltersense/<instance-id>/test/observations`; `/api/state` reports the exact topic. This is not an ESPectre-compatible feed. See [protocols](docs/protocols.md) for the strict envelope. `./scripts/test-mqtt.sh` creates a disposable loopback-only Mosquitto broker, runs the real transport test and removes its test container/resources.

The application is a local foundation without user authentication. Bind to loopback for local use; authenticated remote hosting is outside this task. No external analytics, update checks or hosted PolterSense services are configured.

## Development and boundaries

[Build/validation commands](BUILDING.md) · [Architecture and extraction inventory](docs/foundation.md) · [Contracts, ESPectre reference and next adapter](docs/protocols.md) · [Validation evidence](docs/validation.md).

BLE discovery/identity/distance estimation, old locators/optimizers/history, firmware catalog/OTA/control, MCP and HA device trackers have been removed from implementation and startup, not hidden in navigation. The Medium accepts normalized observations plus scene snapshots; raw batches/capture references would require separately validated, versioned payload types. No unlimited JSON extension bus or job scheduler is present.

## Attribution and license

PolterSense is a fork of ESPresense Companion. Its application infrastructure and retained map components originate with the upstream project and contributors; the fork does not claim authorship of their work. The original [Apache 2.0 LICENSE](LICENSE) is retained unchanged. Historical upstream references identify provenance. ESPectre is a separate project; no ESPectre implementation code is vendored, and no hardware compatibility or sensing accuracy is claimed here.
