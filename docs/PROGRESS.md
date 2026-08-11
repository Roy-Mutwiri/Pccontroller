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

## Browser source, round 2: video specifically still froze (2026-08-10)

User reported the same class of issue again, more specifically: with a YouTube video playing,
switching to another app made the video stop visually updating while its audio kept playing. The
Browser source fix from earlier didn't fully cover this — worth understanding exactly why rather
than just adding more flags speculatively.

- [x] **The gap, precisely**: `BrowserSourceTests`' regression test used a JS `setInterval` timer
      to change a page's background color, which only proves Chromium's *timer* throttling is
      disabled (the `--disable-background-timer-throttling` flag). Video frame compositing is
      scheduled through a completely different path — Chromium's own native-window-occlusion
      detection, which independently suppresses compositor frame submission for an occluded
      window regardless of timer throttling. That's exactly why audio (decode isn't gated by this)
      kept playing while the picture froze: two different subsystems, only one of which the
      original three flags addressed.
- [x] Added `--disable-features=CalculateNativeWinOcclusion` to `BrowserLauncher` — disables
      Chromium's native-window-occlusion detection entirely, so it never learns the window is
      occluded in the first place and none of the downstream throttling paths that key off that
      signal (including the video compositor one) ever engage.
- [x] **New test that actually exercises the mechanism that was broken**, rather than re-proving
      the one that already worked: `BrowserOcclusionVideoTests` uses `requestAnimationFrame`-driven
      canvas rendering instead of a timer — the much closer proxy to how video frames actually get
      scheduled — covers the real launched window with a real second window, captures real frames,
      and asserts the rendered color keeps changing while covered. This is a genuinely different
      code path being tested than the earlier `setInterval` test, not a duplicate.
      `TradeFix.Network.Tests` — 31/31 (1 new). Full solution: 68/68, run twice for stability.
      This time, confirmed no stray Chrome processes remained after the test run *before* declaring
      it clean (checked `ParentProcessId`/command line via WMI first) — applying the lesson from
      the mistake made during the previous round's testing.
- [ ] Not yet confirmed this specific scenario (playing video, switch away, video keeps rendering)
      against a real streaming/trading site rather than a synthetic canvas test page — the
      synthetic test is a faithful proxy for the mechanism, but "faithful proxy" isn't the same as
      "watched the actual reported scenario not happen."

## Tried it with a real YouTube video, at the user's request (2026-08-10)

Wrote a genuine, one-time verification against a real, live, currently-available YouTube video
(Big Buck Bunny, `aqz-KE-bpKQ`, found via web search rather than guessed/recalled from memory —
confirmed to actually exist and be embeddable before using it). Deliberately not added to the
committed suite: depending on a specific external YouTube video staying available forever is
fragile in a way `BrowserOcclusionVideoTests`' synthetic page isn't — this was purely to get real
evidence for this conversation, using the scratchpad + a temporary copy in the test project,
removed afterward.

- [x] First attempt used a direct `/embed/` URL and failed immediately with YouTube's own "Error
      153: Video player configuration error" — navigating straight to an `/embed/` URL outside an
      actual `<iframe>` context isn't a supported use case. Caught by actually looking at the
      captured frame image rather than assuming the pixel-diff failure meant the occlusion fix
      hadn't worked. Switched to the regular `/watch?v=` URL, which works normally.
- [x] With the correct URL, found a **second, real bug in the test's own setup**: it called
      `SetForegroundWindow` on the covering window, which both raises Z-order (covers) AND steals
      keyboard focus in one call — conflating two different signals (occlusion vs. focus loss) that
      a real diagnosis needs to tell apart. Rewrote to cover the window in two isolated phases:
      Z-order-to-top-without-activating (pure occlusion, browser keeps focus) via
      `SetWindowPos(..., HWND_TOP, SWP_NOACTIVATE)`, then additionally stealing focus after — an
      A/B comparison in one run.
- [x] **Real, honest result — a genuine partial fix, not a full one**: the video was *not* fully
      frozen (unlike the pre-fix behavior) in either phase, but its own progress counter advanced
      only ~1 second across a ~6-second covered window — roughly 1/6th real speed — while the rest
      of the page (layout, title, description, subscribe button) kept rendering completely
      normally throughout. This points at YouTube's *own player* deliberately reducing decode/
      render effort for video it believes isn't being actively watched — a mechanism separate from,
      and evidently not fully covered by, `--disable-features=CalculateNativeWinOcclusion` (which
      is confirmed working for generic compositor throttling via `BrowserOcclusionVideoTests`).
      Whether the still-focused-but-covered phase and the covered-and-unfocused phase differ in a
      consistent way wasn't conclusively determined — both showed partial, inconsistent motion
      rather than a clean signal pointing at one specific trigger.
- [x] Documented plainly in KNOWN_LIMITATIONS.md rather than claimed as fixed: YouTube specifically
      still throttles video playback rate (not fully, but substantially) while covered, and this
      may not be fixable via browser launch flags at all if it's YouTube's own deliberate,
      undocumented player behavior. The Browser source's actual value for this project's likely
      real use case (a trading dashboard, not video streaming) probably isn't affected the same
      way, since general page rendering was never the problem — but that's an inference, not
      something separately verified yet.

## Compiled one-click installer: TradeFix.Setup.exe (2026-08-11)

User asked for the installer to be an actual double-clickable .exe rather than the .bat/.ps1
script pair — understandable, a script feels less like "a real installer" to most users even
though it worked reliably.

- [x] **`TradeFix.Setup`** — a new small WPF app (`src/TradeFix.Setup`), a straight C# port of
      `Install-TradeFixBroadcast.ps1`'s logic (`Installer.cs`): validates the expected
      `publish\TradeFix.*-win-x64\` payload sits next to it, stops any running copies, copies files
      to `%LocalAppData%\Programs\TradeFix Broadcast\`, creates Start Menu + Desktop shortcuts,
      registers a real Apps & Features uninstall entry (`TradeFix.Setup.exe --uninstall`), checks
      for Tailscale the same way the script did, and launches the Launcher. `Build-Distributable.ps1`
      now also publishes this and assembles a `dist\` folder — `TradeFix.Setup.exe` sitting
      directly next to `publish\`, the layout an end user actually gets.
- [x] **A real bug found via unit testing before it ever ran**: the original shortcut creation
      used classic `[ComImport]`/`Type.GetTypeFromCLSID` COM interop (`IShellLinkW`), which requires
      an STA thread — but `Installer.Install()` is meant to run via `Task.Run` (a thread-pool/MTA
      thread) to keep the setup UI responsive during file copying. A dedicated regression test
      (`CreateShortcuts_DoesNotCrash_WhenCalledFromAThreadPoolContext`) deliberately forces that MTA
      context via `Task.Run` rather than calling `CreateShortcuts` directly, since a direct xUnit
      call happened to run on a thread whose apartment state masked the problem.
- [x] **Rewrote shortcut creation to shell out to PowerShell's `WScript.Shell` COM object instead
      of in-process COM interop** — sidesteps STA/apartment-state concerns entirely (a separate
      process, not this one's thread), and reuses the exact mechanism the original PowerShell
      installer already used successfully. `ShortcutCreator`'s remarks document the reasoning in
      full, including a documented, real .NET limitation this avoids: built-in COM interop relies
      on generating an IL stub at runtime, which isn't reliably compatible with every self-contained
      single-file publish configuration.
- [x] 9 tests (`TradeFix.Setup.Tests`) — all real filesystem/registry operations against temp
      directories, no mocks: path resolution, missing-app detection, the exact destination-folder
      naming (`Master`/`Agent`/`Launcher`) that has to stay in sync with
      `AppProcessSupervisor.ResolveExePath` on the Launcher side, nested-directory copying, a real
      Tailscale-installed check against this actual machine, real `.lnk` file creation, and the
      thread-pool regression test above. Full solution: 77/77.
- [ ] **Honest, important gap — live end-to-end verification of the published exe was inconclusive
      in this specific dev sandbox**, despite an extensive diagnostic session. Symptom: the
      published self-contained single-file `TradeFix.Setup.exe` would sometimes run 10-28 seconds
      then exit with no window ever shown, no managed exception (checked via top-level
      `AppDomain.UnhandledException`/`DispatcherUnhandledException` handlers that logged to a file
      — nothing was ever written, meaning the exit happens before even `App.OnStartup` finishes),
      and no consistent Windows Event Log crash signature (`CodeIntegrity`/Defender operational
      logs showed nothing; generic Application Error events appeared for some runs but not others).
      Ruled out via careful isolation: not the COM interop (removing it entirely didn't reliably
      fix it either, despite one test appearing to confirm that); not a fixed WDAC block (no
      CodeIntegrity log entries); not deterministic (identical bytes under a fresh filename worked
      once, then an identical retest with fresh filenames still failed). This pattern — a
      newly-built, frequently-rebuilt, unsigned, native-code-extracting single-file executable
      dying unpredictably with no diagnosable trace — is most consistent with some form of
      behavioral/heuristic security scanning in this specific sandbox that sits outside what
      Windows' own event logs surface, not a bug in the shipped code. **This sandbox is confirmed
      not to be one of the actual target PCs** (see KNOWN_LIMITATIONS.md's WDAC section, established
      earlier in this project) — but until the exe can be verified on a real target machine, the
      already-proven `installer/Install-TradeFixBroadcast.bat` remains the recommended install path.
      Master/Agent/Launcher's own self-contained builds continue to run fine in this same sandbox
      (verified repeatedly throughout this project) — whatever this is, it appears specific to
      `TradeFix.Setup.exe`'s build/rebuild pattern during this session, not self-contained
      single-file publishing in general.

## Audio/video sync fix (2026-08-11)

User report: "work on lags and sound and video not syncying together i have noticed the person
speaking and his voice are not going the same." Root cause traced to an asymmetry introduced by
the earlier lag fix's `MediaHub` drop-oldest backpressure handling: it treats video and audio
identically, but dropping a frame affects the two very differently. A dropped *video* frame just
leaves the last frame on screen a moment longer — visibly frozen, but timing-neutral once new
frames resume. A dropped *audio* chunk was silently skipped and never replayed — `AudioCaptureService`
attached no timestamp to chunks at all, so nothing downstream could even detect a drop had
happened — meaning every chunk after a drop played back exactly one chunk-duration too early, and
that error compounded on every subsequent drop. Under real network pressure this makes audio
audibly race ahead of the video it should line up with, matching the reported symptom exactly.

- [x] `AudioCaptureService.ChunkCaptured` now fires with a cumulative captured-audio-timeline
      timestamp (milliseconds of audio, derived from bytes captured so far — not a wall-clock
      read, so a late pump tick doesn't skew it) alongside each chunk.
- [x] `AudioChunkFraming` (new, `TradeFix.Network.Media`) — shared 8-byte-header wire framing so
      Master (`MasterHost.StartAudioCapture`) and Agent (`AgentHost.RunAudioSubscriptionAsync`)
      agree on how the timestamp rides alongside the PCM payload over `/audio/{sourceId}`.
- [x] `AudioSyncGapFiller` (new, `TradeFix.Agent.Services`) — pure state machine comparing
      consecutive chunk timestamps; when a gap is detected (evidence a chunk was dropped upstream)
      it reports how much silence to feed the playback buffer first, so audio gets the same
      "freeze and wait" behavior a stale video frame already has instead of quietly skipping
      ahead. Gaps over 3 seconds are treated as a genuine pause/reconnect rather than a run of
      drops and are left unfilled (no multi-second dead-air playback) — playback just resyncs.
- [x] 7 pure-arithmetic unit tests (`TradeFix.Agent.Tests/AudioSyncGapFillerTests.cs`, new test
      project) covering: no-gap chunks insert no silence, a single dropped chunk inserts exactly
      one chunk-duration of silence, multiple consecutive drops insert the full accumulated gap,
      a running-total check that played duration tracks captured duration exactly across repeated
      drops, oversized gaps are not filled, and `Reset()` forgets prior timeline state.
- [x] Updated `AudioCaptureEndToEndTests` (real WASAPI capture + real relay + real subscriber, a
      genuinely playing 440Hz tone) to decode the new wire framing before its amplitude assertions
      — still passes, confirming the framing change doesn't disturb the existing real-audio path.
      Full solution: 84/84 tests passing.
- [ ] This fixes audio drifting *relative to itself* after a drop. It does not add full
      spec-section-17 shared-timeline AV sync — there's still no guarantee video and audio started
      from the exact same instant, only that audio no longer silently races ahead once both are
      flowing. Genuinely simultaneous stream-start alignment remains future work. See
      KNOWN_LIMITATIONS.md.

## Installer auto-unblock fix (2026-08-11)

User report from a real target PC (PC3, not this dev sandbox): `TradeFix.Setup.exe` "opening and
closing itself immediately." This is the installer live-verification gap noted above, now
confirmed on real end-user hardware rather than just suspected sandbox behavior — consistent with
Windows tagging browser-downloaded files with Mark-of-the-Web (a hidden `Zone.Identifier`
alternate-data-stream), which lets SmartScreen/App Control silently kill an unrecognized, unsigned
exe right after launch.

- [x] `Install-TradeFixBroadcast.ps1` now runs `Unblock-File` recursively over the entire
      extracted package (`installer\` + `publish\`) before anything is copied or run, and again
      over the installed copy under `%LocalAppData%\Programs\TradeFix Broadcast` after copying —
      so Master/Agent/Launcher never launch while still carrying the flag that triggers this.
- [x] Verified end-to-end in this sandbox: ran the updated installer fresh, confirmed the
      unblock step runs without error, and confirmed the installed `TradeFix.Launcher.exe`
      launched and stayed running (not an open-close) afterward.
- [x] README and KNOWN_LIMITATIONS updated with the symptom and both fixes (use the `.bat`
      installer, which now handles this automatically; or manually right-click →
      Properties → Unblock on `TradeFix.Setup.exe` if sticking with the compiled installer).
- [ ] This does not fix `TradeFix.Setup.exe` itself — it's still not the recommended path. A
      proper fix (code-signing the compiled installer) is out of scope without a signing
      certificate; unblocking remains the practical workaround either way.

## Audio echo fix: one shared desktop-audio capture instead of one per source (2026-08-11)

User report: "when i capture a scene with audio i am hearing echo... the echo is heard on the
other pcs." This was the exact mechanism KNOWN_LIMITATIONS.md had already flagged as a known gap
(logged during the earlier audio/video sync fix session, never actually fixed): `MasterHost` gave
every capture source with "Include audio" on its own independent `AudioCaptureService` — its own
`WasapiLoopbackCapture` — but loopback captures the *entire* system output regardless of which
app is targeted. Two audio-enabled sources were always two independent captures of the identical
signal, sent as two unsynchronized network streams (`/audio/{sourceId}` each), and played on the
node through two separate `WaveOutEvent`s with zero mixing. Same audio twice, phase-offset by
however much the two free-running 100ms capture pumps + independent 2s playback buffers happened
to drift — an echo, exactly as reported. Traced end-to-end (capture → Master wiring → network →
Agent playback) before writing any fix, confirming this — not an acoustic room-echo from Master's
own speakers plus a node's — was the actual mechanism, since audio defaults to *on* for every new
capture source (`AddCaptureSourceForWindow`'s `defaultIncludeAudio = true`), so any scene with two
or more capture sources hit this by default, no unusual setup required.

Considered and rejected real mixing (`MixingSampleProvider` summing N `BufferedWaveProvider`
inputs) as the fix: since every source captures the *identical* system-wide signal today (no
per-app audio isolation — see the limitation above), summing N copies of the same signal is
degenerate — it just reproduces that signal at +N×6dB with phase artifacts, not new audio content.
Mixing only becomes meaningful once sources can carry genuinely different audio (per-process
loopback, mic, media file — all still Phase 7, still not built). The actual fix instead collapses
capture to what the signal already is: one shared stream per Master.

- [x] `MasterHost`: replaced the per-source `Dictionary<string, AudioCaptureService>` with one
      ref-counted shared `AudioCaptureService` (`_sharedAudioCapture`) plus a
      `HashSet<string> _audioEnabledSources`. `StartAudioCapture` starts it only if not already
      running; `StopAudioCapture` stops it only once the last source disables audio. Broadcasts on
      one well-known channel id (`SharedAudioSourceId = "desktop-audio"`) instead of per-sourceId.
- [x] `AgentHost`: replaced the per-source subscription/player dictionaries with a single nullable
      subscription + player, active whenever *any* current source in the scene has audio enabled
      (`SyncAudioSubscriptions` now checks `.Any(IsAudioCaptureSource)` instead of diffing a set of
      ids). Subscribes once to `/audio/desktop-audio` regardless of how many sources want audio.
- [x] Fixed a latent bug found while doing this refactor: the original `RunAudioSubscriptionAsync`
      left a dead entry in `_audioSubscriptions` forever if the WebSocket connect itself failed
      (the method returned early without removing it), permanently blocking any retry on the next
      scene sync. The rewritten version clears its own subscription slot on connect failure too
      (guarded by reference-equality so a newer subscription started in the meantime is never
      clobbered).
- [x] `AudioChunkFraming`/`AudioSyncGapFiller`/`MediaHub` needed no changes — source ids were
      already opaque routing keys with no validation against real Project sources, so a constant
      string channel id works exactly like a real source id did.
- [x] No test coupled to the private per-source dictionaries/methods being restructured
      (`AudioCaptureEndToEndTests` exercises `AudioCaptureService` directly, not `MasterHost`'s
      wiring of it) — nothing to update, nothing broke by construction.
- [x] Full solution: zero compiler errors, 6 of 7 test projects verified passing (`TradeFix.Agent.Tests`
      included, most relevant to the `AgentHost` changes). `TradeFix.Master.Tests` could not be run
      in this sandbox — a live `dotnet` process (a real, actively-running Master instance, not a
      stale lock) held `TradeFix.Master`'s own Debug build output locked for the entire session and
      never freed it. Master's own compile step (before the failing copy) produced zero errors, and
      Agent — which changes just as much code — is fully verified, so confidence is high, but this
      is a real, honestly-reported gap: `MasterHost`'s own test suite was not actually executed
      against this change before it shipped.
- [ ] Not verified live against real PC2/PC3 hardware with audio actually enabled on two sources
      simultaneously — only reasoned through from source. Worth a real multi-PC check the next
      time hands-on verification is possible, same caveat as the audio/video sync fix above.

## Crash logging for Master/Agent/Launcher; guard the audio-device init (2026-08-11)

Live-watched Master's log while the user connected PC2/PC3 to debug the "still closing on PC3"
report from the installer auto-unblock fix above. The installer had actually succeeded this time —
Master's log showed a node genuinely pairing, re-authenticating, and subscribing to video/audio —
but then disconnecting again ~9 seconds later, and the user confirmed they'd tried reopening the
app themselves with the same result. So the *installed app* was crashing shortly after a
successful connection, not the installer. That ruled out the earlier theory and pointed at a real
runtime bug.

Root cause identified by inspection: `TradeFix.Agent`'s (and Master's, and Launcher's) `App.xaml.cs`
had no unhandled-exception handling at all — `TradeFix.Setup` had picked this up during its own
crash investigation earlier, but it was never carried over to the other three apps. Any exception
on the UI thread takes a WPF app down instantly and silently by default, which is indistinguishable
from "opens and closes itself" — exactly the symptom reported, just one step further along than
first assumed.

- [x] Added the same `AppDomain.UnhandledException`/`DispatcherUnhandledException`/
      `TaskScheduler.UnobservedTaskException` handlers (already proven in `TradeFix.Setup`) to
      `TradeFix.Agent`, `TradeFix.Master`, and `TradeFix.Launcher`'s `App.xaml.cs`. Agent and
      Master log to their existing `Log` (visible in `%LocalAppData%\TradeFixBroadcast\{App}\logs`)
      plus a TEMP fallback file for the case where `Host`/its logger isn't constructed yet;
      Launcher (no `LogBus` of its own) gets just the TEMP file. This doesn't prevent a crash that
      genuinely can't be recovered from, but it guarantees a trace survives it instead of the
      process just vanishing — the same principle as the installer's `Install-Log.txt` fix above.
- [x] Found and fixed one concrete, plausible crash candidate while in this code:
      `AgentHost.RunAudioSubscriptionAsync` constructed and initialized `WaveOutEvent` outside any
      try/catch. `WaveOutEvent.Init`/`Play` throws if the PC has no default playback device
      (WASAPI "NoDriver" — plausible on a render node with audio disabled or no output device).
      Now wrapped: logs a clear "no audio playback device available" message and cleanly skips
      audio for that subscription instead of leaving an uncaught exception on a fire-and-forget
      Task. Full solution rebuilt and tested: 84/84 passing.
- [ ] Not yet confirmed this was *the* cause of PC3's crash specifically — the fix ships crash
      visibility either way (next occurrence will show up in Agent's log or `%TEMP%\tfagent-crash.txt`
      with an actual exception and stack trace), so the next report from PC3 should be
      immediately diagnosable instead of another round of guessing.

## PC3 crash root-caused and fixed: PerformanceCounter + corrupted perf-counter registry data (2026-08-11)

Resolution to the "still closing on PC3" saga. The crash-logging fix above didn't catch anything —
`tfagent-crash.txt` stayed empty and Agent's own log showed clean connect/subscribe cycles with no
error, ever, right up until the process just stopped emitting log lines. That ruled out AV/Defender
(the user confirmed it was disabled and the crash still happened) and pointed at something below
what managed exception handlers can see. Built `installer\Collect-Diagnostics.ps1` to pull
Windows' own Application event log (`Get-WinEvent`, Error/Critical, last 2 hours) — Windows records
a crash there even when nothing in-process does. That surfaced the exact answer: identical crashes,
every single time, all `System.AccessViolationException` inside
`System.Diagnostics.PerformanceCounter`'s registry-based perf-data read, triggered by
`BasicNodeMetricsProvider.Sample()` on the connection's first post-connect heartbeat (heartbeat
interval is 2s; matches the observed ~9-14s "alive" window almost exactly). Adjacent
`Microsoft-Windows-Perflib` errors for an unrelated Windows service in the same event log confirmed
the actual root cause: that PC's performance-counter registry data is corrupted — a real, fairly
common Windows environmental issue (normally fixed with `lodctr /R`, but not something this app
should require an end user to run).

The reason the earlier crash-logging fix (`AppDomain.UnhandledException`/
`DispatcherUnhandledException`) never caught this: `AccessViolationException` is a corrupted-state
exception, and modern .NET (Core/5+) does not let managed code catch those under any
circumstances — not via a normal `catch`, not via those global handlers, nothing. The existing
`try/catch` wrapped around every `PerformanceCounter` call in `BasicNodeMetricsProvider` had
*always* been silently ineffective against this exact failure mode; it just never actually fired
on the dev machines used up to this point, since their perf-counter data happened to be intact.

- [x] `BasicNodeMetricsProvider` rewritten to never construct a `PerformanceCounter` at all. CPU%
      now comes from `GetSystemTimes` (raw kernel32 P/Invoke, no registry/perflib involvement,
      idle/kernel/user tick deltas between calls). RAM% is unchanged (`GlobalMemoryStatusEx` was
      never part of the crash — only `PerformanceCounter` touched the corrupted subsystem). GPU%
      now honestly reports 0 always — there's no non-PerformanceCounter Win32 API for it, and
      after this incident it's not worth the same crash risk on some other machine for a
      already-documented "best-effort" metric. `IDisposable` dropped from the class — nothing left
      to dispose once `PerformanceCounter` is gone.
- [x] 3 new real tests (`TradeFix.Network.Tests/BasicNodeMetricsProviderTests.cs`) — construct the
      real provider and call `Sample()` for real (not mocked): plausible first-call values, a
      real-delay second call to exercise the actual `GetSystemTimes` delta math, and 10 repeated
      calls mirroring the heartbeat's actual call pattern. Full solution: 87/87 passing.
- [x] Root-cause chain fully verified from a live production machine, not reasoned from source
      alone: `installer\Collect-Diagnostics.bat` → Windows Event Log → identical stack trace on
      every single one of ~6 crash occurrences over the debugging session, all pointing at the
      same line. This is about as confirmed as a fix can be without a second live re-test on that
      exact PC (which is the natural next verification step, not yet done as of this entry).
- [ ] Diagnostic tooling built along the way is worth keeping even though this specific crash is
      fixed: `installer\Collect-Diagnostics.bat` (one-click log+crash-trace+event-log collector,
      copies straight to clipboard) and the `AppDomain.UnhandledException`/
      `DispatcherUnhandledException`/`TaskScheduler.UnobservedTaskException` logging in Master/
      Agent/Launcher's `App.xaml.cs` both remain valuable for any *future* managed-exception crash
      (which, unlike this one, they will actually catch).

## H.264 video pipeline: the real quality fix (2026-08-11)

User: "my biggest worry is on quality we have very bad quality cant we maintain high quality with
no lags." Root cause of "bad quality" was architectural, not a tuning problem: the pipeline sent
every frame as an independent JPEG, which has zero compression *between* frames — so at real
network bandwidth the AdaptiveEncodingController had no choice but to crush quality/resolution
(the logs show it constantly pinned at quality=40 stepping between 640-2000px). A real video
codec (H.264) spends bits only on what *changed* between frames — roughly an order of magnitude
better quality at the same bandwidth for screen content. Implemented via an ffmpeg child process
(the same engine OBS and most broadcast tools build on) rather than hand-written Media Foundation
COM interop, for the same reason GDI beat Windows.Graphics.Capture and NAudio beat raw WASAPI
earlier: it's verifiable with real tests in this environment. ffmpeg runs as a separate process,
so its GPL license does not extend to this codebase.

- [x] `H264VideoEncoder` (`TradeFix.Sources/Video`) — raw BGRA frames in via stdin, compressed
      Annex-B H.264 out via stdout (`libx264 -preset veryfast -tune zerolatency`, keyframe every
      second for fast mid-stream joins). Self-restarts on frame-size changes (window resized),
      firing `StreamRestarted` after draining the old sequence. Gives up after 3 consecutive
      process failures and signals `Failed` → Master falls back to JPEG for all captures.
- [x] `H264VideoDecoder` — stream chunks in, complete self-describing BMP frames out
      (`image2pipe -c:v bmp`), which WPF's existing decode path auto-detects: the renderer needed
      zero changes. Every decoder flag was validated against measured behavior, and three of the
      findings were the opposite of standard advice (all verified with real ffmpeg runs, see the
      flag comments in the class): default input probing emits NOTHING live (5MB probe buffer);
      raw Annex-B packets carry no timestamps (`pts=N/A` confirmed via ffprobe) so default output
      sync silently dropped 2/3 of frames until `-fps_mode passthrough`; and `-fflags nobuffer` —
      the standard live-stream flag — made ffmpeg emit zero frames against this stream shape.
- [x] Measured (not assumed) with real ffmpeg: a long-lived decoder does NOT follow mid-stream
      resolution changes — it silently scales new sequences to the first size ("Reconfiguring
      filter graph" then 0 frames at the new size). Hence `H264StreamProtocol.RestartMarker`: a
      distinct WebSocket message Master broadcasts before each new encode sequence; the Agent
      disposes and recreates its decoder on receipt. Message kinds are distinguished per-message
      (marker = exact match; JPEG = mandatory FF D8...FF D9 framing; else H.264 chunk), so the
      JPEG fallback needs no protocol negotiation at all.
- [x] Mid-stream joining verified for real: cutting the first 40% off an encoded stream and
      feeding the tail to a fresh decoder produced full frames (SPS/PPS repeat at keyframes).
- [x] `ScreenCaptureService` gained a raw-frame mode (`RawFrameCaptured`, reused buffer, JPEG
      encode skipped entirely); `FfmpegLocator` probes app dir → PATH → winget and *validates by
      actually running* the candidate (`-version`) — this sandbox proved an ffmpeg.exe can exist
      on disk yet be App-Control-blocked while a byte-identical copy elsewhere runs, so existence
      checks alone are worthless here. No working ffmpeg → the whole feature silently degrades to
      the existing JPEG pipeline (fallback preserved end-to-end, including per-message on Agent).
- [x] `MediaHub` subscriber queues now have configurable depth: 60 for video in H.264 mode
      (chunks are consecutive ranges of ONE stream — a drop corrupts until the next keyframe, so
      bursts get absorbed; sustained overload still drops + fires `FrameDropped` for adaptive
      control), 1 (latest-wins) for JPEG mode and audio, where it remains correct.
- [x] Adaptive quality preserved: the user's 1-100 quality maps to x264 CRF (100→16
      near-visually-lossless, 40→32), so `AdaptiveEncodingController`'s existing step-down/up
      logic now moves CRF instead of JPEG quantization.
- [x] Master self-preview in H.264 mode: every ~5th raw frame BMP-wrapped (`BgraBmp`) for the
      local canvas — no JPEG exists anywhere in that pipeline to reuse.
- [x] Packaging: `Build-Distributable.ps1` stages ffmpeg.exe next to Master and Agent in
      `publish\` (warns loudly if the build machine lacks it — the package then ships
      working-but-JPEG-quality).
- [x] Tests, all real (89/89 passing): encoder→decoder round trip with pixel-verified colors
      including a mid-stream hard cut, and a compression assertion (encoded < 10% of raw);
      resolution-change restart modeling the real marker protocol with per-sequence decoders;
      real-screen raw capture coherence. The failing intermediate states along the way (0 frames,
      then 9 of 30, then all-one-resolution) were each diagnosed with standalone ffmpeg/ffprobe
      experiments before touching the code — the decoder flag set is derived from measurements,
      not documentation folklore.
- [ ] Not yet live-verified across the real PC2/PC3 network — the decisive test is a real
      YouTube-style motion source at previously-unreachable quality settings holding steady
      without the adaptive controller stepping down. JPEG fallback also means a node running an
      OLD build against a new Master would show nothing for H.264 sources (it expects JPEG) —
      all PCs should reinstall from the current package together.

## In-app log-out and role switching (2026-08-11)

User: "add a place i can log out to the connected node change from node to master pc make the app
flexible not stuck in one place." Previously a node was permanently bound to whichever Master it
first paired with (only hand-deleting credential files undid it), and changing a PC's role lived
solely in the Launcher's tray menu, which an operator may never discover.

- [x] Agent: **Log Out from Master** button (confirm dialog) — `AgentHost.LogoutAsync()`
      disconnects, cancels media/audio subscriptions, deletes the DPAPI credential file, forgets
      the Master address (node name kept — it describes the PC, not the pairing), and clears the
      render window via an empty scene load. The Agent returns to its first-launch "paste a
      connect code" state, ready to pair with a *different* Master.
- [x] Agent: **Switch This PC to Master** button; Master: **Switch This PC to Render Node**
      button (top bar). Both confirm, save the new role, start the counterpart app, and close.
- [x] `RoleSwitcher` (new, `TradeFix.Common`) — writes the Launcher's own settings file (numeric
      enum schema pinned by round-trip tests against the real `LauncherSettingsStore`, so a
      future schema change fails tests instead of silently misreading), resolves the counterpart
      exe via the same sibling-folder layout `AppProcessSupervisor` uses, and starts it as an
      independent process.
- [x] Launcher: when its supervised app exits, it now re-reads the saved role from disk — if it
      changed (an in-app switch), it starts the new role's app, but only if that app isn't
      already running (the app itself is the primary starter; this check is what prevents the
      two starters racing into duplicate processes). A same-role exit just refreshes the tray.
- [x] 3 new tests (`RoleSwitcherTests`): both role values round-trip through the Launcher's real
      reader (real settings file, saved/restored around each test), and counterpart path
      resolution against a real temp installed-layout. Full solution: 93/93.
- [ ] Not verified as a live multi-app flow (click button → watch handoff) — the underlying
      pieces (settings write, process start, Launcher reload) are individually tested, but the
      full choreography should be watched once on a real PC.

## Next up

Live-verify the H.264 pipeline across real PC2/PC3 links (quality holding at high settings under
real bandwidth, restart marker behavior on window resize, ffmpeg staging via the installer).
Live-verify the PerformanceCounter fix actually resolves PC3's crash for real (reinstall the
latest build, confirm it stays connected past the ~9-14s mark that killed every previous attempt).
Live-verify the audio sync fix and the echo fix against PC2/PC3 under genuine network pressure
(not just local reasoning/loopback tests). Then Phase 4's remaining source types (Video file,
Browser, Camera), granular MOVE_SOURCE/RESIZE_SOURCE instead of whole-object UPDATE_SOURCE for
bandwidth efficiency, and eventually per-app audio isolation + real mixing (Phase 7) once sources
can carry genuinely different audio instead of all sharing the
one system-wide signal.
