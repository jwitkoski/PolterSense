# PolterSense foundation

Starting checkout: `cf545051046b4201ec91bb43700c6086030808eb` (clean). Work on `main` at the owner's explicit request; no publication or deployment.

## Extraction inventory

- Keep: ASP.NET Core, SvelteKit/static packaging, health/REST/WebSocket infrastructure, YAML, MQTTnet, map/room polygons, D3 zoom/pan/axes/coordinate tools, local backgrounds, useful UI primitives.
- Adapt: startup and configuration lifetime, immutable versioned scene snapshots, map inventory markers/forms, MQTT reconnect/status and diagnostics, summarized WebSocket state.
- Remove: BLE identities, device/distance models and locators, filters/optimizers/schedules, capture/history/tracker publication/GPS, firmware catalog/OTA/control, MCP, upstream release/dispatch/sponsor/update behavior and obsolete architecture guidance.

## Shared implementation contract

JSON uses camelCase; YAML uses snake_case. Scene `Config`: schemaVersion=1, name, map (flipX/flipY/wallThickness/wallColor/wallOpacity), floors [{id,name,elevation,bounds:[[x,y,z],[x,y,z]],rooms:[{id,name,points:[[x,y]],color?}]}], wisps [{id,name,externalId?,point:[x,y,z],floors:[id],orientation?:[yaw,pitch,roll]}], sources [{id,name,apLabel?,radioId?,bssid?,channel?,point,floors,orientation?}], links [{id,name,wispId,sourceId}]. IDs are explicit, stable and independent of display names. Coordinates are building-wide meters; floor membership never implies radio coverage or inferred motion location. No secrets in this document or scene snapshots. MQTT credentials are environment-only.

REST: GET /api/config -> {revision,config,yaml,error}; PUT /api/config -> {revision,config}; PUT /api/config/yaml -> {revision,yaml}. Both return same shape as GET; expected revision required (409 on conflict), invalid edits 400. GET /api/state and /ws send same summarized state once/second: {schemaVersion:1,configRevision,configurationError,mqtt:{status,testInputEnabled},diagnostics:{accepted,rejected,dropped,stale,duplicate,outOfOrder,retained,lastRejection,lastReceivedAt,latest:[{wispId,sourceId,linkId,receivedAt,acquiredAt,score,motion,ready,provenance,configRevision,freshness}]},medium:{schemaVersion:1,availability:'notImplemented',inputHealth,rooms:[{roomId,state:'unknown'}],tracks:null},demoEnabled}. Configuration is fetched again on revision change. Freshness is separate from MQTT connection status.

POST /api/demo/replay (enabled only by POLTERSENSE_DEMO=true) replays bundled synthetic Echo through the same normalized adapter and processor, using its recorded scene, not current geometry. No device commands, HA entities or inference. See protocols.md for the fixture, timing/provenance rules and pinned ESPectre references.

## Continuity

Foundation implemented and validated on 2026-10-03. Foundation prepared for its initial local commit on `main` at the owner's request; the starting commit above records upstream provenance. No existing user configuration was loaded. The temporary `feature/poltersense-foundation` ref was created before the owner requested direct-main work; it was not used for implementation or merged.

Read README.md and protocols.md first, then validation.md for actual results. Backend tests, frontend type/lint/unit/build, live browser checks, real disposable MQTT integration and local container build/start all passed. Test resources were stopped. Isolated toolchains and logs remain under /tmp/poltersense-tools; these are temporary conveniences, not repository dependencies. Application code is in src, tests in tests/PolterSense.Tests and src/ui, fixtures in fixtures, runnable checks in scripts.

Next observation task: implement live motion ingestion using the verified Direct HTTP identity mapping or Native MQTT with authorized broker access and protocol fixtures. Do not add firmware control, production subscriptions or sensing algorithms by implication. No localization, occupancy accuracy, hardware compatibility, pet detection or calibration/tuning performance has been established.

Demo-install followup: migrated esbuild build approval from removed `onlyBuiltDependencies` to `allowBuilds`; Docker includes the policy during dependency installation. Reproduced pnpm12.8.1 failure and then executed the exact demo script with fresh dependencies/store through successful replay. See the correction section in validation.md; tests no longer rely only on pnpm10 coverage. Changes remain local on main.

Demo-prerequisite followup: the prior native-script test supplied temporary SDKs absent from the owner's shell. Default demo now builds/runs through Docker, seeds a fictional anonymous volume and removes container/data on Ctrl+C; native mode is explicit. `scripts/test-demo.py` excludes host build tools from PATH and verifies startup, replay, isolation and cleanup; CI has a dedicated job. Local test passed. See validation.md for browser checks and the shutdown defect found/fixed during this followup.

Phone-access followup: demo defaults to loopback, with explicit `POLTERSENSE_DEMO_BIND=<local-IPv4>` for LAN access. Launcher tests passed for default and actual host LAN binding; phone/network-path reachability itself remains unverified. See validation.md. No existing user container or network settings were changed.

## ESPectre management followup

Initial foundation committed locally on main as `ad91fdb`. Discovery/configuration work lives on `codex/espectre-discovery` (same checkout). Owner authorized the spare board as a test subject; Native 3.0.0-rc3/ESP32-S3 capabilities and discovery were verified by read-only Direct HTTP. Implemented on-demand mDNS/seed discovery, validated optional Wisp endpoint bindings and explicit label/sensing edits with capability/identity/revision checks and readback. UI is Wisp setup. No real-board settings were changed; write tests use a mock server. No live observations, OTA, Wi-Fi/MQTT configuration or calibration controls were added.

Read src/Espectre, EspectrePanel.svelte and the management section of protocols.md. Validation:111 backend tests plus one broker skip, frontend check/lint/build,6 unit and7 browser tests, Docker launcher regression and real-board read-only mobile UI flow all passed. See validation.md for commands/evidence. No pushes or merges; temporary servers/containers removed. Existing user demo remains under user control; restart it to load new code. Demo volume/data remain intentionally temporary.
