# PolterSense repository guidance

PolterSense is an ASP.NET Core/.NET8 + SvelteKit application extracted from ESPresense Companion. Follow home-directory safety rules and the user's explicit scope. The foundation extraction was explicitly authorized directly on `main`; future work should follow the user's current branch instruction and ordinary feature-branch policy. Do not push, publish, deploy, connect production services or command hardware without authorization.

## Product boundaries

- Read README.md, docs/foundation.md and docs/protocols.md before nontrivial work. Use docs/validation.md for the recorded baseline, not as proof of unrun future changes.
- Application: PolterSense; owned package/topic slug: poltersense. Receiver: Wisp (`wisps`); radio: Source (`sources`); relationship: SensingLink (`links`); estimator: Medium; recording: EchoSession.
- Observations concern radio propagation/source–receiver links. Never manufacture BLE devices, ranges or identities to fit them. Receiver floor/room placement does not locate motion.
- Medium is currently a no-op. Missing/stale/disconnected input is not vacancy. Unknown class and absent tracks/positions are valid. A track ID is anonymous trajectory continuity, not personal identity.
- ESPectre wire names/IDs/capabilities belong to that external protocol. Preserve them in future adapters; application naming does not authorize firmware renaming. No live adapter, OTA/control, HA entity publishing, MCP, optimizers or scheduler exist now.

## Owning components

- `src/Program.cs`: startup, health/REST/WebSocket/static UI; only ConfigLoader, PipelineHost and MqttCoordinator background services.
- `src/Models/Config*`, `src/Services/ConfigLoader.cs`: authoritative bounded/versioned scene validation, snapshot hashing, safe persistence, live refresh. GUI/YAML share this service. Credentials must remain outside scenes and public error details.
- `src/Sensing/`: transport-independent adapter, normalized observations, bounded admission, no-op Medium, deterministic Echo replay. Preserve timing uncertainty, configured-versus-observed metadata and synthetic provenance.
- `src/Services/MqttCoordinator.cs`: optional transport, PolterSense-only test namespace/status, verified TLS, reconnect and retained rejection. Do not restore Companion topic subscriptions, trackers, clearing or controls.
- `src/ui/`: SvelteKit, retained LayerCake/D3 geometry tools, inventory editors and summarized diagnostics.
- `fixtures/synthetic-echo.json`: fictional immutable recorded scene and matching revision. Changing the scene requires deliberately regenerating its hash through ConfigLoader.Snapshot, not reinterpreting old observations with current geometry.

## Development and validation

Use .NET SDK8, Node22, pnpm10. No framework migration or broad dependency upgrade is implied.

The default `./scripts/demo.sh` uses Docker only; host SDKs are needed only for explicit `--native` mode. Run `python3 scripts/test-demo.py` after launcher/packaging changes to catch accidental host-tool dependencies and verify container/volume cleanup.

```sh
dotnet build
dotnet test
pnpm -C src/ui install --frozen-lockfile
pnpm -C src/ui check
pnpm -C src/ui lint
pnpm -C src/ui test:unit --run
pnpm -C src/ui build
pnpm -C src/ui test
python3 scripts/smoke.py
./scripts/test-mqtt.sh
```

Default broker integration test is explicitly skipped without its disposable-broker harness. Browser test supports `POLTERSENSE_E2E_URL` for an isolated real backend and `POLTERSENSE_CHROME` for an explicit browser binary. Never point write-capable smoke tests at production. Docker builds are local-only; use an isolated volume/test container.

C# uses four spaces, PascalCase types and camelCase locals. Svelte/TS uses tabs and existing Prettier/ESLint. Add tests for behavior changes, preserve meaningful infrastructure tests, keep new buffers/payloads bounded. Do not weaken tests to hide failures. Keep original LICENSE and clear upstream attribution; avoid global word replacements in third-party or framework terms.

Use `$context-steward` for substantial work; retain only a compact continuity record in docs/foundation.md and actual validation in docs/validation.md. No OpenProject/governance is required for ordinary local implementation.
