# Observation, Medium and transport contracts

## Implemented boundaries

`IObservationAdapter.Decode(bytes, TransportContext)` returns `NormalizedObservation`; `ObservationPipeline.SubmitAsync(observation, ConfigSnapshot, cancellation)` admits it; `IMedium.EvaluateAsync(observation, snapshot, time, cancellation)` returns a versioned `MediumResult`. These types have no web/MQTT/UI dependency. `TimeProvider` supplies receive time and is injectable. The browser sees summaries once per second, not samples.

Schema/kind version 1 supports only `motion`: nullable boolean motion plus nullable score in [0,1] (at least one present), and readiness/confidence/drop quality. Unknown readiness differs from ready. Firmware version/feature version/detector and observed radio identity/BSSID/channel are optional. Configured Source/link references and separately reported radio metadata have distinct provenance; absent metadata has origin `unknown`. An adapter may not synthesize observed fields from configuration.

Timing preserves nullable UTC acquisition time, optional device-monotonic event milliseconds, receive time, and `clockBasis` (`unknown`, `deviceMonotonic`, `reportedUtc`). `deviceTimeMs` is reported event timing, not proof of acquisition timing. Reported UTC is an upstream assertion, not verified clock synchronization. No arrival-to-acquisition substitution occurs. Diagnostics label absent acquisition time `arrivalOnly`; freshness never proves occupancy or vacancy.

Admission checks versions, payload/value limits, Wisp/Source/link references and scene revision. The queue holds128 observations; diagnostics keep128 latest Wisp/origin/revision entries. Queue overflow is explicitly rejected/counted. Reported UTC older than30s is stale; more than5s ahead is rejected. Queue delay also expires after30s. Dedupe/order checks apply only with both session and sequence; monotonic rollback then also rejects. Without that identity, delivery duplicates cannot be proven. Up to1024 ordering streams are retained per process; new streams beyond that are rejected/counted rather than evicting dedupe history silently. Counters and ordering restart with the process.

The no-op Medium reports `availability: notImplemented`, separate `inputHealth`, all room states `unknown`, and `tracks: null`. Null means no track estimate, not zero targets. Future `EntityTrack` has anonymous ID, class (including unknown), estimate timestamp, optional absolute position and uncertainty; it carries no person's identity. Disconnection is a transport status independent of input freshness. Old scene evidence never becomes current-scene inference.

Future richer batches/raw-capture references require explicit versioned payload types, size limits, provenance and validation. Do not add arbitrary JSON bags. Baseline calibration characterizes the measurement setup; algorithm tuning compares estimators/settings against recorded data; activation is a separate deliberate choice of a reviewable result. None should silently mutate geometry or inference. No optimizer implementation or scheduler is retained.

## Our normalized MQTT test protocol

Enable only in an isolated test environment with `POLTERSENSE_TEST_INPUT=true` and a broker host. The topic reported by `/api/state` is `poltersense/<instance-id>/test/observations`. Only exact matches are subscribed. MQTT5 retain-as-published makes retained test input rejectable both during normal delivery and reconnect. All retained observations are rejected; they never refresh evidence. Only PolterSense status/Last Will is published, never cleared. Credentials are environment-only; scene and revision files cannot contain transport credentials.

Payload limit16KiB, depth16, strict JSON property names (camelCase; parser case-insensitive), unknown/duplicate fields rejected. Example (replace revision and configured IDs with your isolated scene values):

```json
{
  "schemaVersion": 1,
  "kind": "motion",
  "kindVersion": 1,
  "wispId": "wisp-studio",
  "sourceId": "source-ground",
  "linkId": "link-studio",
  "configRevision": "COPY_THE_CURRENT_SCENE_REVISION",
  "sessionId": "test-run-1",
  "sequence": 1,
  "timing": {"acquiredAt": null, "deviceTimeMs": null, "clockBasis": "unknown"},
  "firmware": null,
  "observedRadio": null,
  "motion": {"motion": true, "score": 0.7},
  "quality": {"ready": true, "confidence": null, "droppedSamples": null}
}
```

Receive time and synthetic provenance are assigned by the adapter, never trusted from the publisher. This is **not ESPectre's on-wire format**. `scripts/test-mqtt.sh` uses a real disposable Mosquitto broker and TCP interruption relay; tests exercise nonretained admission, retained rejection, duplicate/malformed input, LWT/reconnect, unchanged foreign retained state and topic isolation.

## Echo replay

`fixtures/synthetic-echo.json` is EchoSession schema1, explicitly synthetic, with a complete scene snapshot and SHA-256 revision. Revision uses the canonical typed .NET scene serialization (property order/defaults included; geometry/list order significant; YAML comments/formatting excluded). Three fixture samples have fixed sequence, session and timing. Replay checks the recorded snapshot hash and every observation revision, then uses the same adapter/admission/Medium classes with an injected deterministic clock and independent bounded ordering state per run. Historical diagnostics are copied into the display buffer with `origin: replay`/`historicalReplay`; original timing and identity are preserved. Replay does not contribute to current live input health. Cancellation stops replay; only one replay runs at a time. The recorded scene is returned in the fixture, while the current displayed map remains current configuration; diagnostics show the recorded revision explicitly.

`POLTERSENSE_DEMO=true` enables POST `/api/demo/replay`. It cannot read a caller-supplied path, control devices, publish HA entities or send MQTT data. Repeated fixture processing is reproducible even if live geometry changes. This is a minimal fixture runner, not a recorder/dataset platform.

## ESPectre reference inspected 2026-10-03

Primary references: [project](https://github.com/francescopace/espectre), [MQTT API](https://espectre.dev/sdk/mqtt-api/), pinned [API](https://github.com/francescopace/espectre/blob/47a36fdcec481e4f4521922c6589746f9ad54395/docs/API.md) and [CSI](https://github.com/francescopace/espectre/blob/47a36fdcec481e4f4521922c6589746f9ad54395/docs/CSI.md). Repository main resolved to `47a36fdcec481e4f4521922c6589746f9ad54395` (2026-10-01); documented application protocol1.0. Recheck capabilities/version against actual firmware before implementation.

- Native MQTT defaults to `espectre/v1/devices/<device-id>`; preserve external topic prefixes/IDs. Retained health/device/capabilities/sensing/wifi are state, not new motion evidence. Motion is nonretained. ESPHome uses its own integration transport, not this Native MQTT subscription surface. [MQTT API](https://espectre.dev/sdk/mqtt-api/)
- Motion carries `timestamp_ms`, `state` and `score`; device timing is monotonic. Readiness belongs to sensing state. Native MQTT wifi omits BSSID, SSID and IP. Do not invent source identity, acquisition UTC, session identity or sequence. Direct HTTP exposes additional resources and is capability-dependent. [Pinned API](https://github.com/francescopace/espectre/blob/47a36fdcec481e4f4521922c6589746f9ad54395/docs/API.md)
- Raw CSI is separately available through capability-gated Direct HTTP `/espectre/v1/csi`, not Native MQTT or SSE. The documented V8 stream has a60-byte prefix and16-byte session ID. Opening capture pauses derived motion and makes readiness false; it is not a passive replacement for the motion subscription. [Pinned API](https://github.com/francescopace/espectre/blob/47a36fdcec481e4f4521922c6589746f9ad54395/docs/API.md)
- Radio propagation/source selection, traffic generation and CSI capture quality are distinct concerns. A traffic generator's IP endpoint does not establish the physical transmitting radio; missing/invalid capture does not establish an empty room. Hardware/profile-specific behavior needs measurement. [Pinned CSI](https://github.com/francescopace/espectre/blob/47a36fdcec481e4f4521922c6589746f9ad54395/docs/CSI.md)

**Next concrete adapter:** implement read-only Native MQTT capability/health/sensing/device/motion ingestion for one explicitly mapped external device ID ↔ stable Wisp ID. Preserve external wire names. Map score/state, known readiness and monotonic event time; leave acquisition UTC, session/sequence and source BSSID unknown unless separately evidenced. Keep configured link assignments visibly configured. First verify actual receiver firmware/capabilities and capture representative sanitized payloads; no access to the owner's EAP670s or receivers occurred here. Source provenance during roaming, reboot/order identity, detector feature versions, and raw CSI radio metadata still require explicit mapping/validation. Firmware operations remain unavailable through `AdapterCapabilities.FirmwareControl=false`.
