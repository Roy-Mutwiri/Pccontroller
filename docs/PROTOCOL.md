# Control Protocol

Implemented in `src/TradeFix.Protocol`. LAN-only by default (spec section 29/9) — the Master never
listens on a public interface by design.

## Envelope

Every control-channel message is a JSON object matching `TradeFix.Protocol.Envelope`:

```json
{
  "protocolVersion": 1,
  "messageId": "b6f1...",
  "timestamp": "2026-08-10T12:00:00Z",
  "type": "heartbeat",
  "projectId": null,
  "sceneId": null,
  "payload": { }
}
```

- `protocolVersion` is the **wire protocol** version (`TradeFix.Protocol.ProtocolVersion.Current`),
  independent of both the application version (Master/Agent build number) and the **project
  schema** version (`ProjectDefinition.SchemaVersion` in `TradeFix.Shared`). Three different
  version numbers, three different reasons to bump them.
- `messageId` is a fresh GUID per message (not currently deduplicated — commands should be
  designed idempotent per spec section 9, so redelivery is safe).
- `payload` is untyped at the envelope level (`JsonElement`); `Envelope.ReadPayload<T>()` and
  `Envelope.Create(type, payload)` handle typed (de)serialization against the concrete message
  record for that `CommandType`.

## Transport

`IMessageTransport` (`TradeFix.Network.Transport`) abstracts "send/receive one Envelope at a
time" over either:
- a real `WebSocket` (`WebSocketMessageTransport` — used by both Master's `HttpListener`-accepted
  connections and the Agent's `ClientWebSocket`), or
- an in-process channel pair (`InProcessMessageTransport` — Simulation Mode only).

Messages are single WebSocket text frames, UTF-8 JSON, one Envelope per frame (no
newline-delimited batching).

## Connection lifecycle (implemented — Phase 1)

```
Agent                                   Master
  │──── connect (WebSocket) ───────────►│
  │◄─── HELLO (serverName, protocolVer, requiresPairing) ────│
  │
  │  [no stored credentials]
  │──── PAIR_REQUEST (code, name, os, appVersion) ──────────►│
  │◄─── PAIR_RESPONSE (approved, nodeId, sessionToken) ──────│
  │
  │  [stored credentials from a prior pairing]
  │──── AUTH_REQUEST (nodeId, sessionToken) ─────────────────►│
  │◄─── AUTH_RESPONSE (success) ──────────────────────────────│
  │
  │──── HEARTBEAT (every 2s: metrics, scene, state) ─────────►│  (repeats)
  │◄─── PING ───────────────────────────────────────────────│
  │──── PONG ──────────────────────────────────────────────►│
```

- Pairing codes are short-lived (10 min default), single-use (`PairingCodeRepository.TryConsume`
  atomically checks-and-marks-consumed), and never transmitted anywhere except operator-to-operator
  (spoken/typed between the two PCs) — they never appear on the wire except inside the
  `PAIR_REQUEST` the Agent sends after a human typed it in.
- Session tokens are high-entropy random strings; only their SHA-256 hash is ever persisted
  (`PairedNodeRepository`), per spec section 29.
- If the connection drops, the Agent reconnects with exponential backoff (1s, 2s, 4s, 8s, 16s,
  capped — `ReconnectPolicy`) and re-authenticates with its stored credentials; no re-pairing
  needed.

## CommandType catalog

`TradeFix.Protocol.CommandType` defines the full catalog from spec section 9, grouped by the phase
that implements them:

| Group | Types | Status |
|---|---|---|
| Connection lifecycle | `Hello`, `PairRequest`, `PairResponse`, `AuthRequest`, `AuthResponse`, `Heartbeat`, `NodeStatus`, `Ping`, `Pong`, `Error`, `Disconnect` | **Implemented (Phase 1)** |
| Project/scene/source state | `LoadProject`, `LoadScene`, `AddSource`, `RemoveSource`, `UpdateSource`, `MoveSource`, `ResizeSource`, `SetVisibility`, `SetOpacity`, `SetLayer` | Phase 2+ |
| Media | `PlayMedia`, `PauseMedia`, `StopMedia`, `SeekMedia`, `SetAudioVolume`, `MuteAudio`, `UnmuteAudio` | Phase 5/7 |
| Output | `StartOutput`, `StopOutput`, `RestartRenderer` | Phase 8 |
| Synchronization | `SyncState`, `RequestStatus` | Phase 2 |

Defining the full enum now (rather than growing it ad hoc) keeps the protocol contract stable —
downstream phases add message *payload* records and handler logic, not new wire concepts.

## Versioning / compatibility (design — not yet enforced)

`Envelope.protocolVersion` lets the Master detect an out-of-date Agent (spec section 28) once a
protocol change actually happens. Phase 1 only ever produces `protocolVersion: 1`, so there's
nothing to gate yet; the field exists so a future incompatible change can reject or degrade
old peers instead of silently misbehaving.
