# Architecture

## Goal

One Master (PC1) holds the authoritative production state — scenes, sources, transforms,
media/audio state. It never streams its own desktop. Instead it pushes structured **state** to
Render Agents (PC2, PC3), which render the production locally using their own hardware. Each PC's
own local streaming software (TikTok Studio, OBS, etc.) captures that PC's local render output.

```
MASTER (PC1)                      RENDER AGENT (PC2)          RENDER AGENT (PC3)
┌─────────────────────┐           ┌───────────────────┐       ┌───────────────────┐
│ Project / Scene /    │  state    │ Local renderer     │       │ Local renderer     │
│ Source state          │ ───────► │ (same scene graph) │       │ (same scene graph) │
│ (authoritative)       │  commands│                     │       │                     │
│ Control-channel server│ ◄─────── │ heartbeat / status  │       │ heartbeat / status  │
└─────────────────────┘           └───────────────────┘       └───────────────────┘
```

## Why C#/.NET 8 + WPF (not Electron, not Tauri/Rust)

Evaluated against this project's actual hard requirements — Desktop Duplication API / Windows
Graphics Capture, WASAPI audio capture, Media Foundation camera capture, GPU-accelerated
compositing, low-latency LAN networking:

- **Electron + React** was the spec's suggested default, but none of the above are reachable from
  Node/Chromium without writing native C++ addons anyway — Electron would add Chromium's bundling
  weight and a JS↔native boundary on top of the same native code we'd need regardless.
- **Tauri + Rust** is architecturally closer (native, small), but was ruled out for *this specific
  development environment*: the dev sandbox this project is built in enforces Windows Defender
  Application Control (WDAC) in an unusually strict mode that blocked `cargo.exe` outright
  ("Application Control policy has blocked this file"), and Rust's ecosystem for Desktop
  Duplication/WGC/WASAPI/Media Foundation is far less mature than .NET's.
- **C#/.NET 8 + WPF** has first-class, actively maintained access to every native API this project
  needs (via CsWin32 / Vortice.Windows bindings), WPF is hardware-accelerated (DirectX-backed)
  with mature multi-window/multi-monitor support, and **WebView2** (Microsoft's Chromium-based
  control) satisfies the Browser Source requirement without us bundling Chromium ourselves.

See the conversation record for the full environment probe (WDAC enforcement status, missing
.NET SDK, presence of VS Build Tools 2022) that informed this decision. This was confirmed with
the project owner before any code was written, since it's a fundamental, hard-to-reverse choice.

## Solution layout

```
src/
  TradeFix.Shared      Domain models: Project, Scene, Source, Node, Transform, DeviceMapping...
  TradeFix.Protocol    Wire protocol: Envelope, CommandType, message payloads, versioning
  TradeFix.Network     Transport abstraction, WebSocket server/client, pairing, auth, heartbeat,
                       reconnect, Simulation Mode
  TradeFix.Database    SQLite migrations + repositories (paired nodes, pairing codes, logs)
  TradeFix.Common      Logging (categorized, multi-sink), AppPaths (no hardcoded paths)
  TradeFix.Rendering   Canvas/compositor abstraction (Phase 3+)
  TradeFix.Sources     Source plugin interface + built-in source implementations (Phase 4+)
  TradeFix.Audio       Audio engine (Phase 7+)
  TradeFix.Assets      Asset hashing/sync/cache (Phase 5+)
  TradeFix.Master      WPF app — Control Center (runs on PC1)
  TradeFix.Agent       WPF app — Render Agent (runs on PC2/PC3)
tests/
  TradeFix.Shared.Tests, TradeFix.Protocol.Tests, TradeFix.Network.Tests
docs/
```

`Rendering`, `Sources`, `Audio`, `Assets` exist now as empty, referenced projects so the
dependency graph and namespaces are stable; they're implemented incrementally per
[DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md). This mirrors the phase plan in the original spec —
building all ten phases in one pass was explicitly ruled out.

## Master ⇄ Agent connection model

See [PROTOCOL.md](PROTOCOL.md) and [NODE_SYSTEM.md](NODE_SYSTEM.md) for the wire format and node
lifecycle. In short: `IMessageTransport` is an abstraction over a bidirectional JSON-envelope
stream. `WebSocketMessageTransport` wraps a real `System.Net.WebSockets.WebSocket` (used by both
the Master's `HttpListener`-accepted server socket and the Agent's `ClientWebSocket`).
`InProcessMessageTransport` wraps a pair of in-memory channels and is used exclusively by
**Simulation Mode**, so fake nodes exercise the *exact same* `NodeSession`/`AgentConnection` code
as real hardware — no separate mock implementation to drift out of sync.

## Data flow for a state change (Phase 2+ — design, not yet implemented)

1. User drags a source in the Master's canvas.
2. Master updates its authoritative `ProjectDefinition` (debounced, not per-pixel).
3. Master broadcasts an `UPDATE_SOURCE` command to all connected nodes.
4. Each node applies the update to its local scene graph and re-renders.
5. Master is authoritative: a node's local render state never diverges except through explicit,
   recorded `NodeOverride`s (spec section 40).

## Why not build everything now

The full spec (56 sections) describes a multi-month, multi-phase system. Per its own instructions
(section 48/54), work proceeds phase-by-phase with a working, tested state after each phase — see
[DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md) and [PROGRESS.md](PROGRESS.md) for what's done vs. planned.
