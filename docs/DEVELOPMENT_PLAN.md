# Development Plan

Phases as scoped in the original spec (section 48). Each phase ends with a build that compiles,
passes its tests, and meets a concrete acceptance test — never left in a knowingly broken state.
Live status: [PROGRESS.md](PROGRESS.md).

| Phase | Deliverable | Acceptance test |
|---|---|---|
| 0 | Repo, architecture, docs, schemas, protocol | Solution builds; docs exist |
| 1 | Master + Agent connection, pairing, auth, heartbeat, node dashboard | Simulated (and real-loopback) nodes reach `Online` on the Master dashboard |
| 2 | Project/Scene/Source state model + sync protocol, reconnect/resync | Changing a test source on Master changes PC2/PC3 |
| 3 | Render engine: canvas, layers, transforms, FPS monitoring | A simple scene renders identically on all nodes |
| 4 | Sources one at a time: Color, Image, Text, Video, Browser, Display capture, Window capture, Camera, Audio | Each source tested on Master + all nodes before moving to the next |
| 5 | Asset sync: hashing, upload/download, cache, resumable transfer | Adding a video on Master auto-propagates to nodes that lack it |
| 6 | Scene system: create/switch/preview/program/transitions | Switching scenes on Master switches all nodes |
| 7 | Audio engine: sources, mixer, device mapping, sync | Audio plays with acceptable latency/CPU across nodes |
| 8 | Output: local render surface, OBS integration, virtual camera (see [OUTPUT.md](OUTPUT.md)) | Each node's local streaming software can capture its own render |
| 9 | Diagnostics + monitoring: health, FPS/CPU/GPU/RAM, logs, alerts | Diagnostics tool reports PASS/WARNING/FAIL per subsystem |
| 10 | Polish: UI, error handling, onboarding, installer, auto-start, backups | Installer produces a working first-run experience |

## Current phase: 1 complete, ready to start Phase 2

Phase 1 scope actually delivered:
- `TradeFix.Shared` — full Project/Scene/Source/Node/DeviceMapping schema (used by later phases,
  not just Phase 1's connection needs — defining it once now avoids a breaking schema migration
  later).
- `TradeFix.Protocol` — versioned envelope, full `CommandType` catalog, Phase-1 message payloads.
- `TradeFix.Network` — transport abstraction (real WebSocket + in-process), pairing, DPAPI-backed
  credential storage, heartbeat, reconnect with backoff, Simulation Mode.
- `TradeFix.Database` — SQLite migrations, paired-node and pairing-code repositories.
- `TradeFix.Master` / `TradeFix.Agent` — WPF dark-theme shells; Master shows a live node
  dashboard + pairing flow + log panel; Agent shows connection setup + pairing prompt + log panel.
- Tests: schema unit tests, protocol round-trip tests, two Network integration tests (in-process
  simulated pairing, and a *real* loopback WebSocket pairing/heartbeat run) — all passing.

Deliberately not started: anything from Phase 2 onward (no scene/source rendering, no media, no
audio, no output integration). Building those now would mean guessing at a state-sync protocol
before Phase 1's connection layer had been proven to actually work end-to-end.
