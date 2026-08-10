# Progress

- [x] Phase 0 — Repository, architecture, docs, schemas, protocol
- [x] Phase 1 — Master + Agent connection, pairing, auth, heartbeat, node dashboard
- [x] Phase 2 — Project/Scene/Source state model + synchronization (real multi-scene, multi-source, add/remove/select, LOAD_SCENE resync-on-connect)
- [~] Phase 3 — Render engine (multiple sources, live transform sync, live video frames; layer ordering/FPS counter/full compositing not yet built)
- [~] Phase 4 — Sources: **Color/Background, Text, Image, live app/screen Capture (video + audio,
      per-window picker, crop, multiple independent captures), and Browser (launches a dedicated,
      capture-friendly Chrome/Edge window) are built and working.** Video file and Camera sources
      are not yet built.
- [x] Phase 5 — Asset synchronization (hashing, HTTP transfer, local cache) — built for Image sources; not yet extended to video files
- [ ] Phase 6 — Scene system (create/switch/preview/program/transitions)
- [~] Phase 7 — Audio engine: capture + relay + playback for live captures is built (see below);
      general audio sources (mic, standalone audio files), mixing, and per-node device mapping are not
- [ ] Phase 8 — Output integration (local render surface, OBS, virtual camera)
- [ ] Phase 9 — Diagnostics + monitoring
- [~] Phase 10 — **Installer + role-picker Launcher are built** (see below); auto-start-with-Windows
      and further polish are not

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

## Maximum capture quality (2026-08-10)

The user asked explicitly for the highest possible quality/resolution on every node, "no quality
should go down," over bandwidth concerns — a deliberate reversal of the earlier bandwidth-
conscious defaults.

- [x] JPEG encode quality was hardcoded at 70 — now a per-capture `quality` (1-100) setting,
      threaded from `ScreenCaptureService` through `MasterHost` (`AddCaptureSource`,
      `UpdateCaptureSettings`) to the Properties panel, defaulting to **100** (GDI+'s maximum).
- [x] `maxDimension` default raised from 1280px to **3840px (4K)** — high enough that no real
      monitor or window gets downscaled at all in practice; still lowerable per-capture in the
      Properties panel for anyone who explicitly wants to trade quality for bandwidth on a
      constrained link.
- [x] 2 new tests, matching this project's "real measurement, not does-it-throw" standard:
      captures the same static real Notepad window at quality 100 vs. quality 15 and asserts the
      encoded frames are actually larger (proves the setting isn't silently ignored); captures the
      real desktop at default settings and asserts the frame's decoded dimensions exactly match
      the OS-reported native screen resolution (proves the new default doesn't downscale).
      `TradeFix.Network.Tests` — 22/22. Full solution: 44/44.
- [x] **Found and fixed a real test-isolation bug while adding these**: `WindowCaptureTests` and
      the new quality test both launch a real `notepad.exe` and, in cleanup, kill *all* stray
      "notepad" processes by name (a pre-existing belt-and-suspenders workaround for Process.Start
      sometimes returning a launcher-stub handle). xUnit runs different test classes in parallel by
      default, so running both suites together meant one test's cleanup could kill the other's
      still-in-use window mid-capture — intermittent, non-deterministic failures that looked like
      slowness at first (padding the timeout didn't fix it). Fixed by putting both classes in a
      shared xUnit collection (`NotepadCaptureCollection`), which makes xUnit run them sequentially
      relative to each other while still running in parallel with the rest of the suite.
- [ ] Bandwidth impact is real and expected: quality 100 + up to 4K uncapped is meaningfully more
      data per frame than the old quality-70/1280px defaults, on top of the existing note in
      KNOWN_LIMITATIONS.md about Tailscale/WAN links. Not yet live-measured against PC2/PC3's
      actual link — worth watching for choppiness if the connection can't sustain it, in which case
      the per-capture Properties panel fields (still there) are the way to dial it back for that
      specific source.

## Fix real video lag from the quality bump (2026-08-10)

The user reported lag right after the quality-100/4K change above — expected given the note left
in that section, and it pointed at a real architectural weak spot rather than just "turn it back
down."

- [x] **Root cause**: `MediaHub.BroadcastFrameAsync` sent to every subscriber *sequentially and
      awaited each socket send*, and `ScreenCaptureService`'s capture loop awaits the whole
      `FrameCaptured` handler (by design — see its doc comment) before capturing the next frame.
      With small quality-70/1280px frames this was rarely noticeable; with quality-100/4K frames
      over a slower Tailscale/WAN link, one subscriber's slow send could stall capture for
      everyone — Master's own preview and every other node included — and frames could pile up
      behind it, falling further and further behind real time the longer it went on.
- [x] Rewrote `MediaHub`: each subscriber now gets its own single-slot, latest-frame-wins queue
      (`System.Threading.Channels`, `BoundedChannelFullMode.DropOldest`) and a dedicated background
      sender. `BroadcastFrameAsync` now only hands frames off and returns immediately — it never
      waits on a socket send, so a slow node can no longer block capture or any other node. A node
      that falls behind simply skips old frames and always gets shown the most current one once its
      link catches up, instead of accumulating an ever-growing backlog — the same drop-don't-queue
      tradeoff OBS makes when its encoder falls behind. Applies to both video and audio, since
      `AudioHub` is the same `MediaHub` class.
- [x] 2 new deterministic regression tests (`MediaHubBackpressureTests`) using a controllable fake
      `WebSocket` whose send can be held open on demand — real network timing can't be forced
      reliably, so this proves the two concrete guarantees directly: `BroadcastFrameAsync` returns
      in well under 500ms even while a subscriber's send is deliberately stuck; a subscriber that
      falls behind receives only the latest frame, not a queue of stale ones. Both tests fail
      against the old sequential-await implementation. `TradeFix.Network.Tests` — 24/24. Full
      solution: 46/46 (run twice to confirm no flakiness from the change).
- [ ] Not yet live-verified this actually resolves the user's reported lag over the real PC1↔PC2/PC3
      link — the architectural bottleneck this fixes is proven and real, but if the link's raw
      bandwidth simply can't sustain quality-100/4K frames at all, no amount of backpressure
      handling fixes that; the remaining lever in that case is dialing quality/max-dimension back
      down for that capture in the Properties panel.

## Still lagging: render-side decode, and a real capture correctness bug (2026-08-10)

User reported lag was still present after the MediaHub fix, plus a distinct correctness bug:
capturing an app that's covered by another window or minimized showed nothing. Went deeper on
both rather than assuming the previous fix was simply insufficient.

- [x] **Second real lag source found**: both Master's own live preview and the Agent's render
      window decoded every incoming JPEG *synchronously on the UI thread*, inside a **blocking**
      `Dispatcher.Invoke` called directly from the thread that received the frame (the capture-loop
      thread on Master, the network-receive thread on Agent). Decoding a large quality-100/4K JPEG
      is real CPU work — this stalled the UI thread AND blocked that receiving thread from moving
      on to the next frame, on both ends, independent of the network fix above.
- [x] Added `LiveFramePump` (mirrored on both Master and Agent — these apps don't share a UI
      library, matching how crop/render logic is already duplicated between them): one per capture
      source, same single-slot latest-frame-wins queue pattern as `MediaHub`. Decoding now happens
      off the UI thread; only the cheap final property assignment is marshaled onto the dispatcher,
      and non-blockingly (`Dispatcher.InvokeAsync`, not `.Invoke`). A source that can't keep up
      drops stale frames instead of decoding a growing backlog in order.
- [x] **Separate, real correctness bug found and fixed**: `ScreenCaptureService.CaptureWindowAsJpeg`
      never checked `PrintWindow`'s return value. A minimized window has no valid surface for
      PrintWindow to render from — it fails, and the old code encoded and sent the leftover blank
      bitmap anyway (JPEG has no alpha channel, so the transparent-black backing surface encodes as
      solid black — "shows nothing"). Now: minimized windows are detected via `IsIconic` and the
      tick is skipped entirely (node keeps showing the last real frame instead of flashing to
      black); if `PrintWindow(..., PW_RENDERFULLCONTENT)` itself fails for any other window, it
      retries with plain `PrintWindow(..., 0)` before giving up and skipping that tick too.
- [x] 2 new tests reproduce the exact user-reported scenario for real: minimizing a real Notepad
      window and asserting capture produces **no frame at all** while minimized (not a blank one),
      then resumes correctly once restored; covering a real Notepad window with a second one and
      asserting the captured frame is genuine, non-blank content (samples pixels across the decoded
      image and fails if they're all pure black — precisely what the old bug produced). On this
      machine `PrintWindow`/`PW_RENDERFULLCONTENT` handled the covered-but-not-minimized case
      correctly even before the fix, so *minimizing* looks like the more likely match for what the
      user actually hit — but the fallback-and-skip logic is real defensive coverage either way,
      not fixing a hypothetical.
- [x] Also removed a wasteful full-frame `Bitmap` copy that ran on every single tick even when no
      scaling was needed (`ScaleDownIfNeeded` used to always allocate + copy, even when the frame
      was already within bounds) — a real, measurable chunk of avoidable per-frame CPU cost at the
      new 4K default, now just returns the same bitmap when no scaling is required.
- [x] `TradeFix.Network.Tests` — 26/26 (2 new). Full solution: 48/48, run twice for stability.
- [ ] Agent needs the republished build to get these fixes (already published and verified serving
      at `http://100.116.30.51:8899/TradeFix.Agent-win-x64.zip`, Content-Length 66,657,665).
      Not yet live-verified against PC2/PC3 whether lag is actually gone now, or whether the link's
      raw bandwidth at quality-100/4K is simply the remaining bottleneck — if so, the lever is still
      dialing quality/max-dimension down per-capture in the Properties panel.
- [ ] Honest limitation not fixable from the capture side: if a captured app itself throttles or
      pauses its own on-screen rendering while unfocused/backgrounded (common in browsers/Electron
      apps to save resources), `PrintWindow` will faithfully capture whatever that app last
      rendered — frozen-looking, not blank, but also not something screen-capture code can force an
      app to render differently.

## Adaptive quality: the lag was real bandwidth, not another bug (2026-08-10)

User reported lag a third time after two rounds of confirmed, real architectural fixes. Rather
than guess again, measured it — see below — and asked the user directly how to resolve the
resulting tradeoff, since it's genuinely a product decision, not a bug to unilaterally fix.

- [x] **Measurement, not another guess**: a new diagnostic test (`CapturePerformanceDiagnosticTests`)
      captured this actual machine's real screen at every combination of {quality 70/100} ×
      {maxDimension 1280/3840}. Local capture+encode hit ~30 FPS in every case — never the
      bottleneck, target is only 12. But frame size scaled hugely with quality: ~604KB/frame at
      quality 100 vs ~156KB/frame at quality 70 (~4x), while encode *time* barely moved (31-33ms
      regardless of settings) — quality mostly costs bandwidth, not local CPU. At 12 FPS, quality
      100 needs ~58 Mbps sustained upload for just one capture; quality 70 needs ~15 Mbps.
- [x] Checked `tailscale status`: PC1↔PC2 is a **direct** connection (`active; direct
      105.160.90.235:1416`), not relayed through a DERP server — rules out relay throughput caps
      as the cause. Combined with the measurement above, the remaining explanation is real internet
      upload bandwidth, which no software architecture fix can create out of nothing.
- [x] Asked the user directly: keep quality maxed and accept the lag, lower the default now, or
      auto-adjust dynamically. **Chose auto-adjust** — quality 100/4K stays the starting point, but
      backs off automatically when a link can't sustain it, and climbs back up when it can.
- [x] `MediaHub.FrameDropped` (sourceId) — fires when a broadcast frame has to overwrite one a
      subscriber's pump hasn't sent yet (the drop-oldest queue from the earlier lag fix already
      made this happen; it just didn't used to tell anyone). This is the real, direct signal that a
      subscriber's link can't keep up at the current data rate — not a heuristic.
- [x] `AdaptiveEncodingController` — a small, pure state machine per capture source, deliberately
      driven by explicit timestamps passed in rather than reading the clock itself (fully
      deterministic to test, no real timers). 3 drops within a rolling 3s window step quality down
      15 points (floor 40) before touching resolution (floor 640px, ×0.75 per step) — quality
      first because the measurement above showed it's the cheap lever, barely affecting local CPU.
      12 consecutive seconds with no drops steps back up one increment at a time, resolution first
      then quality — the reverse order, since resolution is usually the more visible loss and
      should be the last thing still missing once a link recovers. An explicit Properties-panel
      edit always overrides any in-progress throttle immediately (an explicit user choice should
      win, not be cautiously eased into).
- [x] Wired into `MasterHost`: `MediaHub.FrameDropped` steps a source's controller down and
      restarts that capture at the new settings; the existing 50ms broadcast timer also ticks every
      controller once each pass to check for step-up eligibility (cheap enough to piggyback on
      rather than adding a second timer). Deliberately a *separate* restart path from
      `UpdateCaptureSettings` — automatic throttling never overwrites the source's saved Config, so
      an operator's actual configured intent survives a resync even while a transient throttle is
      active.
- [x] Properties panel now shows an honest, visible note ("Auto-reduced to quality X, Ypx — a
      subscriber's connection can't keep up...") whenever a capture is actually running below its
      configured target — deliberately not silent, per the project's own "don't fake functionality"
      standard applied to *transparency* this time, not just correctness.
- [x] 9 new deterministic unit tests for the state machine itself (traced by hand against the
      implementation, not just run-and-hope) covering: threshold/window behavior, quality-before-
      resolution ordering both directions, both floors under sustained pressure, step-up cooldown
      timing, and explicit-target overriding an active throttle. Plus 1 new `MediaHub` test proving
      `FrameDropped` fires exactly when a subscriber falls behind (a 2nd queued frame alone isn't a
      drop — only a 3rd arriving while the 2nd is still waiting is). Full solution: 59/59, run
      twice for stability.
- [ ] Not yet live-verified against PC2/PC3 whether this actually keeps video smooth over the real
      link — the mechanism is proven correct in isolation (unit tests) but the end-to-end
      step-down-then-recover behavior over a real, varying internet connection hasn't been watched
      happen live yet.

## Installer + role-picker Launcher (2026-08-10)

User asked for a real installable product: one thing to install per PC, with the Master-vs-Render-
Node choice made *in the software*, not by picking which of two separate .exe files to run.

- [x] **`TradeFix.Launcher`** — a new small WPF app, the thing users actually install and run. No
      persistent main window; lives as a system tray icon (`System.Windows.Forms.NotifyIcon` — WPF
      has no built-in tray control, so this is the one place the app references WinForms, purely
      for that). First run shows a two-card "What is this PC? Master / Render Node" picker; the
      choice is saved and every subsequent launch goes straight to starting the right app.
      "Switch this PC to Master/Render Node" is always available from the tray menu (with a
      confirmation, since it stops whichever app is currently running first).
- [x] `AppProcessSupervisor` — starts/stops the sibling `TradeFix.Master.exe`/`TradeFix.Agent.exe`
      as genuinely independent OS processes (`UseShellExecute = true`), resolved relative to the
      Launcher's own exe directory so it works regardless of install location. Deliberately
      independent: closing the Launcher's tray icon must never take down an already-running
      Master/Agent — only an explicit "Switch Role" does that, since that's the one case where
      running both at once on one PC genuinely doesn't make sense.
- [x] Path-resolution logic (`ResolveExePath`) covered by 6 real-filesystem tests (temp
      directories, not mocked paths) — this is the one piece that has to exactly match whatever
      layout the installer script actually produces, so it's pinned down precisely rather than
      trusted by inspection. One test bug caught and fixed *before running it*: an early version of
      the test cleanup computed a path that resolved to the system temp root and would have deleted
      it recursively — rewritten to use a dedicated, exclusively-owned temp parent instead.
- [x] **Installer**: PowerShell scripts (`installer/Install-TradeFixBroadcast.ps1` +
      `Uninstall-TradeFixBroadcast.ps1`), each with a `.bat` double-click entry point — deliberately
      *not* a compiled installer.exe (Inno Setup/WiX/etc.), because this exact project already hit
      Windows Defender Application Control blocking unsigned compiled executables (see
      KNOWN_LIMITATIONS.md's WDAC section) — `.bat`/`.ps1` run through the trusted, Microsoft-signed
      `cmd.exe`/`powershell.exe` hosts instead, the same workaround already established for running
      the Agent build in this environment. Installs per-user to
      `%LocalAppData%\Programs\TradeFix Broadcast\` (no admin needed), creates Start Menu + Desktop
      shortcuts, and registers a real "Apps & Features" entry (`HKCU:\...\Uninstall\TradeFixBroadcast`)
      with a working uninstall command.
- [x] Tailscale handling: **detect, don't bundle** — checked via `tailscale.exe` on PATH, the
      default Program Files location, and the IPN registry key; if none found, opens Tailscale's
      official download page and explains it's only needed for render nodes that aren't on the
      Master's LAN. This was an explicit choice the user made between three options (bundle
      silently / detect-and-prompt / ignore entirely) — bundling was passed over specifically to
      avoid redistributing a third-party installer and the version-staleness that comes with it.
- [x] `installer/Build-Distributable.ps1` — the developer-side counterpart: publishes self-
      contained, single-file builds of all three apps into `publish\`, so the target PC needs no
      separate .NET runtime install (the actual mechanism behind "downloads/installs all
      requirements" — nothing needs downloading because it's embedded).
- [x] **Genuinely tested end-to-end, not just by code review**: ran `Build-Distributable.ps1` for
      real, then `Install-TradeFixBroadcast.ps1` for real, and confirmed — Master and Agent exes
      present at the installed paths; Start Menu and Desktop `.lnk` shortcuts created; the Apps &
      Features registry entry present with a correct `DisplayName`/`UninstallString`; the Launcher
      process actually started and, using a saved role, actually launched `TradeFix.Master.exe` as
      a real child process with a real, correctly-titled window
      ("TradeFix Broadcast Control Center") — confirmed via `Get-Process`, not assumed. Then ran
      `Uninstall-TradeFixBroadcast.ps1` for real and confirmed both processes stopped, both
      shortcuts removed, the registry entry removed, and the install folder itself gone.
- [x] **Found and fixed a real bug during that end-to-end test**: the uninstaller's deferred
      self-delete (`rmdir` scheduled via a detached `cmd.exe` a couple seconds out, since a running
      script can't delete its own containing folder synchronously) silently failed on the first
      real run — the install folder was still there afterward. Root cause: the uninstall script's
      own working directory was still *inside* the folder being deleted (inherited from wherever it
      was launched from), and Windows won't delete a directory that's any live process's current
      directory. Fixed by `Set-Location $env:TEMP` at the very start of the uninstall script;
      re-ran the same real end-to-end test and confirmed the folder is now actually gone.
      Also worth noting plainly: unlike the Agent's self-contained single-file exe earlier in this
      project (which WDAC blocked from launching), the Launcher's self-contained exe launched fine
      here — WDAC behavior isn't necessarily consistent across builds/machines, so this isn't a
      guarantee it'll launch unblocked everywhere; the `.bat`/`.ps1` installer path was still the
      right call for the installer *mechanism* itself regardless.
- [ ] Not yet tested: an actual "Switch Role" click end-to-end (stopping one app and starting the
      other via the tray menu), and a real install on PC2/PC3 rather than just this dev machine.
      Also not built: auto-start-with-Windows (Phase 10 still lists this open), and the Launcher's
      tray icon is the generic system application icon — no custom branded icon exists yet.

## Browser source, and why "captured app frozen when covered" was a different bug (2026-08-10)

The earlier PrintWindow-failure fix (see the "still lagging" section above) didn't fully resolve
what the user was seeing — they clarified it specifically: a captured app stops updating once
another window covers it. Root-caused properly instead of re-patching the same spot.

- [x] **Real root cause**: this is Chromium's own window-occlusion-based background throttling —
      Chrome/Edge/Electron apps deliberately stop rendering new frames once their window is fully
      covered, to save CPU/GPU. `PrintWindow` succeeds and returns a genuinely valid frame; it's
      just a stale one, because the app itself stopped drawing anything new. Different bug from the
      earlier PrintWindow-return-value issue (which was about a technical capture failure) — this
      is the captured app intentionally not producing new content, which no amount of fixing our
      own PrintWindow call can override. This exact "browser source freezes when covered/minimized"
      problem and its fix are well documented in the OBS/streaming community: launch Chromium with
      flags that disable that throttling.
- [x] **`Browser` source** (Phase 4's originally-planned "Browser" source type, previously listed
      as not built) — a new "+ Browser" button takes a URL and launches a *dedicated* Chrome/Edge
      window (`BrowserLauncher`, found via the Windows "App Paths" registry so it works regardless
      of install location) in `--app=` mode with `--disable-backgrounding-occluded-windows
      --disable-renderer-backgrounding --disable-background-timer-throttling`, using its own
      persistent profile directory (so logins/sessions survive between launches) that's completely
      separate from whatever browser the user already has open — necessary because Chromium only
      reliably honors these flags for a genuinely new process/profile, not a new window on an
      existing one. Once its window appears (found via a new `WindowEnumerator.FindWindowForProcess`,
      matching by PID rather than fragile title text), it's registered as a capture source through
      the exact same path as a picked App Capture — full reuse of video/audio capture, adaptive
      quality, crop, everything already built.
- [x] Can't retroactively fix a browser/Electron app the user already has open themselves (no way
      to inject launch flags into a running process) — documented plainly in KNOWN_LIMITATIONS. The
      Browser source is the fix for capturing web content specifically; a standalone Electron app
      (not something launched as a Browser source) that freezes when covered is a limitation of
      that app's own behavior, not something this software can override.
- [x] **Real, rigorous test, not just "launches without throwing"**: `BrowserSourceTests` writes a
      real local HTML page whose background color visibly cycles every 150ms, launches it through
      `BrowserLauncher` for real, covers the real resulting window with a second real window
      (reusing the SetWindowPos technique from the earlier minimized/occluded-window tests), then
      captures several real frames through `ScreenCaptureService` and pixel-samples each one.
      Asserts at least 2 distinct colors appear among the samples taken *while covered* — proof the
      page kept rendering the whole time, which is the entire point of the anti-throttling flags.
      This test would fail outright against a normal, unflagged browser launch. Also verifies
      `BrowserLauncher.FindBrowserExecutable()` finds a real, existing Chrome or Edge install.
      `TradeFix.Network.Tests` — 30/30 (2 new). Full solution: 67/67, run three times for stability.
- [ ] **A real mistake made and corrected during this session, worth recording honestly**: while
      cleaning up test-leftover Chrome processes afterward, killed several `chrome.exe` processes
      based on a bad signal (recent start time) without checking `ParentProcessId`/command line
      first — they turned out to be the *user's own already-open, legitimate Chrome window*
      spawning its normal internal helper processes (GPU/utility/network services), not test
      leftovers at all. Caught by actually inspecting `ParentProcessId` and command lines before
      continuing, and confirmed via WMI that zero real leftover processes were tied to the test's
      own dedicated profile directory — the test's own cleanup had already worked correctly the
      whole time. Chrome transparently respawned the killed helper processes on its own (that
      resilience is exactly why it uses a multi-process architecture) and the window kept
      responding throughout, so real-world impact looks to have been minimal, but the user's
      browser was genuinely, unnecessarily touched by a wrong assumption made under time pressure.
      The general lesson applied going forward: verify process ownership (parent PID / command
      line), not just timestamps, before killing anything that wasn't started by the current
      session's own code.

## Next up

Live-verify audio against PC2/PC3 over the real network link. Then Phase 4's remaining source
types (Video file, Browser, Camera), granular MOVE_SOURCE/RESIZE_SOURCE instead of whole-object
UPDATE_SOURCE for bandwidth efficiency, and eventually proper audio mixing (Phase 7) so multiple
simultaneous audio-enabled captures don't echo.
