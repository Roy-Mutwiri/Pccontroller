# Progress

- [x] Phase 0 — Repository, architecture, docs, schemas, protocol
- [x] Phase 1 — Master + Agent connection, pairing, auth, heartbeat, node dashboard
- [x] Phase 2 — Project/Scene/Source state model + synchronization (real multi-scene, multi-source, add/remove/select, LOAD_SCENE resync-on-connect)
- [~] Phase 3 — Render engine (multiple sources, live transform sync, live video frames; layer ordering/FPS counter/full compositing not yet built)
- [~] Phase 4 — Sources: **Color/Background, Text, Image, and live app/screen Capture (video +
      audio, per-window picker, crop, multiple independent captures) are built and working.**
      Video file, Browser, and Camera sources are not yet built.
- [x] Phase 5 — Asset synchronization (hashing, HTTP transfer, local cache) — built for Image sources; not yet extended to video files
- [ ] Phase 6 — Scene system (create/switch/preview/program/transitions)
- [~] Phase 7 — Audio engine: capture + relay + playback for live captures is built (see below);
      general audio sources (mic, standalone audio files), mixing, and per-node device mapping are not
- [ ] Phase 8 — Output integration (local render surface, OBS, virtual camera)
- [ ] Phase 9 — Diagnostics + monitoring
- [ ] Phase 10 — Polish, installer, auto-start

## Phase 1 detail (complete, 2026-08-10)

- [x] `TradeFix.Shared` domain schema (Project/Scene/Source/Node/DeviceMapping/Transform/...)
- [x] `TradeFix.Protocol` envelope + `CommandType` catalog + Phase-1 payloads
- [x] `TradeFix.Database` SQLite migrations + paired-node/pairing-code repositories
- [x] `TradeFix.Network`: transport abstraction (WebSocket + in-process)
- [x] Pairing flow (code issue → consume → node identity + session token)
- [x] Reconnect-with-stored-credentials (auth flow)
- [x] DPAPI-encrypted credential storage on the Agent
- [x] Heartbeat (2s interval) with CPU/RAM/GPU metrics
- [x] Exponential-backoff reconnect
- [x] Simulation Mode (`NodeSimulator`, in-process transport, real state machine)
- [x] Master WPF shell: dark theme, node dashboard (live cards), pairing panel, log panel
- [x] Agent WPF shell: connection setup, pairing prompt, status, log panel
- [x] Unit tests: schema (`TradeFix.Shared.Tests`), protocol round-trip (`TradeFix.Protocol.Tests`)
- [x] Integration tests (`TradeFix.Network.Tests`): simulated single node, two simulated nodes,
      real loopback WebSocket pairing/heartbeat run, single-connect-code auto-pair flow — 23/23
      tests passing
- [x] Both apps launch without crashing; Master's control port verified live via a real HTTP
      request (see verification note below)
- [x] Real-world test with a physically separate PC2 over Tailscale surfaced two real gaps, both
      fixed: (1) manual IP/port/pairing-code entry was error-prone (operator pasted PC2's own
      Tailscale IP by mistake) — replaced with one auto-detected, copy-pasteable connect code;
      (2) Agent required a manual "Connect" click every launch — now auto-reconnects using saved
      settings/credentials after the first successful pairing. See NODE_SYSTEM.md "Pairing".

### Verification note (what was actually checked, and what wasn't)

Checked directly: solution builds with 0 errors; full test suite passes (14/14, including a real
WebSocket loopback test, not just the in-process simulator); both WPF apps launch and stay running
without an unhandled exception; the Master's `HttpListener` genuinely accepts a connection on its
configured port (verified with a real HTTP request returning the expected 400 for a
non-WebSocket-upgrade request).

**Not checked**: actually clicking "Pair New Node" / "Add Simulated Node" in the running Master
UI and visually confirming the card appears — there's no UI-automation tool available for WPF in
this environment (unlike the browser tooling available for web UIs). The underlying logic those
buttons call is the same `PairingService`/`NodeSimulator` code exercised directly by the
integration tests, so this is a reasonably low-risk gap, but it is a gap, not a claim that it was
visually confirmed.

## Real-world test log (2026-08-10)

Beyond automated tests, this was validated against a genuinely separate physical PC2 connected
over Tailscale (not same-room LAN, not a simulated node):

- [x] PC2 paired successfully — confirmed in Master's SQLite `paired_nodes` table and log, not
      just the Agent's own UI claim (an earlier "it's connected" report turned out to be PC2
      pointed at itself — see NODE_SYSTEM.md; this was caught by cross-checking Master's own data
      rather than trusting the UI alone).
- [x] Auto-reconnect validated for real: Master was restarted (to ship the Phase 2/3 slice below)
      while PC2 was live-connected; PC2's Agent detected the drop and re-authenticated
      automatically with zero user action, confirmed in the log (`Node ... re-authenticated`).
- [x] Pairing-code default validity raised 10→30 minutes after a real first-time setup (file
      transfer + extract + run) genuinely took longer than 10 minutes in practice.

## Phase 2/3 minimal slice (2026-08-10): live scene sync proof-of-concept

Before building the full scene/source editor, built the smallest possible end-to-end slice to
prove the actual hard part — Master state changes reaching a real remote node live — works at
all: `MasterHost.DemoSource`, one hardcoded colored box, draggable/resizable on the Master's new
"Program" canvas (`MainWindow.xaml` `ProgramCanvas`), broadcast via a new `UPDATE_SOURCE` protocol
message (`UpdateSourcePayload`, wrapping the existing `SourceDefinition`/`Transform2D` schema
unchanged from Phase 0) to every connected node.

- [x] `MasterServer.BroadcastAsync` / `NodeSession.SendAsync` — Master→node push, not just the
      node→Master direction Phase 1 had
- [x] `NodeSession.Ready` / `MasterServer.SessionReady` — a newly (re)connected node immediately
      receives current state, not just future changes (the resync-on-connect requirement)
- [x] Rate-limited broadcast (`MasterHost`'s 50ms timer, dirty-flag coalescing) instead of one
      network message per mouse-move event
- [x] Agent-side `RenderWindow` — a real second window (not just a status readout) that mirrors
      the box live, intended as the literal local render surface a future OBS/TikTok Studio
      Window Capture would point at (see OUTPUT.md)
- [x] Integration tests: broadcast reaches an already-connected node; a newly-connected node gets
      current state via SessionReady without needing a separate broadcast — 25/25 tests passing
- [x] Verified live against the real PC2: the box appeared in PC2's Render Output window. First
      attempt showed nothing — root cause was both windows defaulting to the same "CenterScreen"
      position, so Render Output was hidden directly behind the status window (fixed by
      positioning them explicitly relative to each other in `App.xaml.cs`), not a sync bug.

## Real scene/source management (2026-08-10)

Replaced the single hardcoded demo box with `MasterHost.ProjectState`: real multiple scenes
(create/switch), real sources (add/remove/select), a Properties panel for editing
text/color, all synced live via the same LOAD_SCENE/UPDATE_SOURCE mechanism proven above.
Verified live against PC2 after fixing the window-overlap bug.

## Image sources (2026-08-10)

- [x] `TradeFix.Assets.AssetStore` — SHA-256-content-addressed local cache (spec section 16: never
      re-transfer a file a node already has), used identically by Master (source of truth) and
      Agent (cache of files fetched from Master).
- [x] `MasterServer` extended with an HTTP `GET /assets/{hash}` endpoint (separate `HttpListener`
      prefix, not multiplexed onto the WebSocket control channel) — how a node fetches a file it
      doesn't have cached.
- [x] Master "+ Image" button (file picker → hash → cache → add source); Agent downloads once and
      renders locally from then on. Position/resize updates after that are tiny (just
      `Transform2D`), not the image again.

## Live screen capture — video mirroring (2026-08-10)

User explicitly asked for true "TikTok-style" mirroring (PC1's exact live screen/app activity,
including cursor movement, shown on PC2) rather than each node capturing its own local instance
of an app — a real architectural fork from the original spec's Window Capture design (spec
section 19), confirmed with the user before building given the scope difference. Audio was
deliberately scoped out as a separate follow-up (see KNOWN_LIMITATIONS.md) — this slice is video
only.

- [x] `TradeFix.Network.Media.MediaHub` + `MasterServer`'s `GET /media/{sourceId}` WebSocket
      endpoint — a binary frame relay, deliberately a third channel separate from both the JSON
      control WebSocket and the asset-download HTTP endpoint (spec section 38: "Separate CONTROL
      CHANNEL and MEDIA/PREVIEW CHANNEL"). Fully covered by integration tests (real WebSocket
      client subscribes, `BroadcastAsync` bytes arrive intact) — this part doesn't depend on
      screen-capture hardware and could be verified automatically.
- [x] `TradeFix.Sources.Capture.ScreenCaptureService` — GDI `BitBlt` + manual cursor compositing
      via `DrawIconEx`/`GetCursorInfo`, JPEG-encodes each frame. Chose GDI over the more modern
      Windows.Graphics.Capture API specifically because WGC's WinRT/COM interop
      (`IGraphicsCaptureItemInterop`, `CreateDirect3D11DeviceFromDXGIDevice`) could not be
      compiled/verified against the real Windows Runtime in this environment — see
      KNOWN_LIMITATIONS.md for the full reasoning.
- [x] **Smoke-tested for real on this machine** (not just automated tests): a throwaway console
      harness ran `ScreenCaptureService` standalone, captured 3 real frames, verified valid JPEG
      framing, and one frame was visually inspected (via the Read tool) and confirmed to be a
      genuine, correct screen capture of this actual desktop — not corrupted or blank data.
- [x] Agent-side: `AgentHost.SyncLiveSubscriptions` opens/closes a media WebSocket per live-capture
      source as scenes change; frames decode to a frozen `BitmapImage` and render in
      `RenderWindow` alongside the other source types.
- [x] First real-world attempt reported "cannot capture apps" — root cause was **delivery
      timing, not a pipeline bug**: the capture-enabled Agent build was published to the PC2
      download link *after* the user had already tested with the prior (pre-capture) build.
      Diagnosed by comparing build/publish timestamps against the Master log's connection
      timestamps, not by guessing. Fixed by publish-then-verify-then-instruct ordering going
      forward.

## Per-window capture, direct delete, real self-preview (2026-08-10)

Follow-up after the user clarified the actual want: capture one specific app (e.g. TradingView),
not the whole screen; multiple different captures side by side; an obvious way to delete one; and
— once they could test — Master's own canvas showed only a static placeholder for captures, never
the actual content, so there was no way to verify a capture was even working without checking PC2.

- [x] `TradeFix.Sources.Capture.WindowEnumerator` + `ScreenCaptureService`'s `targetWindow`
      parameter (`PrintWindow` with `PW_RENDERFULLCONTENT`, cursor position translated into
      window-relative coordinates) — capture one specific app, not the whole monitor. "+ Full
      Screen" kept as a fallback option. Each capture call is independent, so several different
      apps can run side by side.
- [x] **Rigorous test, not a superficial one** (explicitly requested): launches a real Notepad
      window, captures only it, and asserts the frame dimensions are window-sized rather than
      screen-sized — proving the capture actually targets the picked window, not just that
      *some* image came back. `WindowCaptureTests` — 2/2 passing.
- [x] A red "✕" button directly on every source box (all types, not just captures) — deletes that
      specific source immediately, independent of selection state.
- [x] **Found and fixed a real bug** while wiring up the self-preview: `NullToVisibilityConverter`
      only worked for `string` bindings (`value as string` silently yields `null` for any other
      type). Bound to `SelectedSource` (a view-model object, not a string), it always evaluated as
      "empty" — meaning **the entire Properties panel had been permanently invisible** no matter
      what was clicked, since the app was first given multi-source support. Neither the panel nor
      its "nothing selected" fallback ever rendered. Fixed and covered by a dedicated regression
      test (`TradeFix.Master.Tests`, new project — 6/6 passing) so a fix for one binding can't
      silently break another.
- [x] Master's own canvas now shows the real live capture (Master already has the JPEG bytes
      in-process from its own `ScreenCaptureService.FrameCaptured` — no need to round-trip through
      its own network relay to preview what it's sending), with a name badge, instead of a static
      "streaming, not previewed here" placeholder.
- [x] Verified live against PC2 — Master's log recorded `Media subscriber joined for <sourceId>`
      after the picker-based capture, confirmed via the log rather than taken on faith.

## Crop editing + capture settings (2026-08-10)

`Transform2D.Crop` existed in the schema since Phase 0 but nothing read or wrote it until now —
requested by the user for the captured scene specifically ("edit crop... and more settings").

- [x] Crop (Left/Top/Right/Bottom, edited as 0–100% in the Properties panel) wired up for Image
      and Capture sources, rendered via the standard "overscale and clip" technique (no custom
      Geometry/Transform needed) — applied identically on Master's own preview and every Agent's
      render, both driven by the same `Transform2D.Crop` synced over the wire.
- [x] Capture-only settings — frame rate and max dimension (quality/bandwidth) — editable after
      creation; applying restarts that specific capture at the new values without needing to
      delete and re-add it.
- [x] **Found a second real bug** while implementing this: dragging/resizing a source rebuilt a
      bare `Transform2D` from just X/Y/Width/Height, silently discarding Crop (and everything
      else Transform2D carries) on every mouse-move. Fixed by having drag/resize go through the
      same `SourceItemViewModel.CurrentTransform()` the crop-apply path now uses, so there's one
      source of truth instead of two divergent code paths. Covered by a regression test that
      simulates a drag and asserts crop survives it.
- [x] 5 new tests for the crop math itself (values, not just "doesn't throw") — e.g. cropping 25%
      off both left and right should exactly double the rendered content width and shift it left
      by exactly the cropped region's rendered size. `TradeFix.Master.Tests` — 11/11 passing.
      Full solution: 41/41.
- [x] **Follow-up (same day)**: the typed-percentage-only Crop fields weren't a good enough
      interaction — the user asked for direct edge-dragging, "like how TikTok does it". Added four
      accent-colored drag handles fixed to a selected Image/Capture source's own edges (the same
      model OBS Studio's crop tool uses): dragging a handle inward increases that edge's crop
      fraction live, using the same "update locally + push through `UpdateTransform`" pattern
      drag/resize already established, so it broadcasts to nodes during the drag rather than
      needing a separate Apply step. The typed percentage fields stay, now just for precise
      fine-tuning, and reflect drag changes live since both paths write the same properties.
      Clamped so opposing edges can never combine past 90%, leaving the box non-degenerate.
      Verified the app still launches cleanly (no XAML parse errors) after the change; the actual
      drag gesture itself is UI-interaction code with no automated coverage in this environment,
      matching the same documented limitation as the original drag/resize handles (see
      KNOWN_LIMITATIONS.md) — needs a live check from the user.

## Audio capture + playback (2026-08-10)

The deliberately-deferred second half of "TikTok-style" mirroring — the user asked for it
explicitly once video was working. WASAPI loopback (desktop audio), not per-app isolated audio
(see KNOWN_LIMITATIONS.md for why, same tradeoff reasoning as GDI-vs-WGC for video).

- [x] `TradeFix.Sources.Audio.AudioCaptureService` — WASAPI loopback capture via NAudio, resampled
      to a fixed 16-bit PCM format for consistent playback regardless of the system's actual
      output device configuration. One instance per capture source (toggleable via a new
      "Include audio" checkbox in the Properties panel), so different captures can each have
      audio on/off independently.
- [x] A third relay channel — `MasterServer`'s `GET /audio/{sourceId}` — reusing the same
      `MediaHub` class already proven for video, now generalized (`HandleBinarySubscriberAsync`)
      rather than duplicated. Video, audio, and control remain three independent WebSocket
      channels (spec section 38), each tolerant of the others dropping out.
- [x] Agent-side playback via NAudio (`BufferedWaveProvider` + `WaveOutEvent`), one player per
      subscribed audio source, opened/closed in step with `SyncLiveSubscriptions`' existing
      video-subscription lifecycle pattern.
- [x] **A real, non-obvious bug found and fixed, not just "it compiled so it's done"**: the first
      resampling implementation (`MediaFoundationResampler`, NAudio's Media-Foundation-backed
      resampler) measurably produced near-silent output for roughly the first second of every
      capture session — every time, regardless of resampler quality setting (tried quality 1 and
      30, identical result). This was invisible to any test that only checks "did a chunk of the
      right byte-length arrive" — it takes measuring actual sample amplitude against genuinely
      *playing* audio to catch it. Diagnosed methodically: proved the raw WASAPI capture was fine
      (94.6% peak amplitude from the very first callback) before suspecting the resampler; then
      confirmed the same silence pattern was resampler-specific, not a pacing bug, by testing
      NAudio's pure-managed `WdlResamplingSampleProvider` chain instead — which worked correctly
      from the first read. Rewrote `AudioCaptureService` around the working chain.
- [x] Two rigorous "harder" tests added, matching the standard set earlier in this project (real
      capture, not synthetic bytes): a standalone smoke test played a real 440Hz tone through
      NAudio's own output and confirmed the capture pipeline measured genuine, sustained amplitude
      (not just the first chunk); `AudioCaptureEndToEndTests` codifies the same check permanently
      — real tone → real capture → real `MasterServer` relay → real WebSocket subscriber → asserts
      non-silent PCM arrived. This exact test would have failed against the original
      MediaFoundationResampler implementation. Full solution: 42/42 passing.
- [ ] Not yet live-verified against PC2/PC3 with real hardware audio output — the pipeline is
      proven correct end-to-end on this machine; whether it sounds right over the actual
      PC1↔PC2/PC3 Tailscale link still needs a live test with the user.

## Next up

Live-verify audio against PC2/PC3 over the real network link. Then Phase 4's remaining source
types (Video file, Browser, Camera), granular MOVE_SOURCE/RESIZE_SOURCE instead of whole-object
UPDATE_SOURCE for bandwidth efficiency, and eventually proper audio mixing (Phase 7) so multiple
simultaneous audio-enabled captures don't echo.
