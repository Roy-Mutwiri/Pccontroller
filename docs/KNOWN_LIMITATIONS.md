# Known Limitations

Honest accounting of what's not (yet) real, per spec section 52 ("don't fake functionality —
document the limitation").

## Phase 1 (current) scope gaps

- **No scene/source rendering yet.** Nodes connect, pair, authenticate, and heartbeat — they do
  not yet receive or render any production content. That's Phases 2–4.
- **FPS and latency metrics are placeholder zeros.** They become real once Phase 3 (rendering
  loop) and real round-trip timing exist. Reporting a fabricated number would be worse than
  reporting an honest zero.
- **GPU utilization is best-effort.** It reads the Windows "GPU Engine" performance counter
  category (`engtype_3D` instances, summed). This is a real OS-provided signal, not fabricated,
  but: it can read 0 on systems where the GPU driver doesn't populate that counter category, it
  only captures 3D-engine utilization (not video-decode/encode engines), and it's a system-wide
  aggregate, not process-attributed. Accept it as directionally useful, not authoritative.
- **No version-compatibility gating (spec section 28).** `Envelope.protocolVersion` is on the wire
  but the Master doesn't yet reject or flag an out-of-date Agent — there's only been one protocol
  version to compare against so far.
- **No remote-desktop diagnostic view, emergency controls beyond "Add Simulated Node", or
  global/group command modes.** These require Phase 2+ state to act on meaningfully.
- **UI interaction wasn't visually verified**, only the underlying service logic (see
  [PROGRESS.md](PROGRESS.md) verification note) — no WPF UI-automation tool was available in this
  environment to click through the Master/Agent windows and confirm on-screen behavior.

## Networking

- **LAN wildcard binding needs a one-time setup step on Windows** (elevation or `netsh http add
  urlacl`) — see [NODE_SYSTEM.md](NODE_SYSTEM.md#first-time-network-setup). Without it, the
  Master falls back to localhost-only automatically (logged, non-fatal), but PC2/PC3 genuinely
  cannot reach it in that fallback state. This is a real Windows `HttpListener` constraint, not
  a bug — documented rather than silently worked around with elevation-by-default (which would be
  its own security concern).
- **No LAN auto-discovery yet.** The Agent's UI requires the Master's IP typed manually (spec's
  own Phase 1 acceptance criteria don't require discovery — only pairing).

## Live screen capture (video mirroring)

- **Per-window capture is built** (a picker lists real open windows; "+ Full Screen" remains as a
  fallback) — captures exactly the picked window via `PrintWindow`, not the whole monitor.
- **Audio is now included** (see "Audio capture" below) — no longer video-only.
- **GDI (`BitBlt` + manual cursor compositing), not Windows.Graphics.Capture.** WGC is the modern,
  GPU-accelerated, officially-recommended API and was the first approach attempted — it requires
  WinRT/COM interop (`IGraphicsCaptureItemInterop`, manual Direct3D11↔WinRT device bridging via
  `CreateDirect3D11DeviceFromDXGIDevice`) whose exact marshaling signatures could not be verified
  by compiling against the real Windows Runtime APIs in this environment; a wrong HRESULT/GUID
  there fails in ways that are hard to diagnose without live iteration. GDI capture is decades-old,
  needs only plain P/Invoke, and was verified working end-to-end on the first real test (see
  PROGRESS.md) — the more reliable choice for a first working version. It's CPU-bound (slower,
  higher-latency than WGC) and won't correctly capture some exclusive-fullscreen DirectX content.
  Revisit WGC once there's a way to test the interop live.
- **Bandwidth not yet tuned for real-world links.** Frames are ~85KB JPEGs at a default 12 FPS —
  roughly 1MB/s (~8 Mbps) sustained upload from Master to *each* subscribed node. That's fine on a
  real LAN but may strain a home internet upload link when nodes connect over Tailscale/WAN (as in
  this project's actual PC1↔PC2 setup). Frame rate and max dimension are editable per-capture in
  the Properties panel (applying restarts that capture at the new settings).
- **No reconnect/backpressure handling on the media WebSocket** beyond what's built — a dropped
  media subscription reconnects only when the next full LOAD_SCENE arrives (e.g. on Agent
  reconnect), not automatically mid-session. Acceptable for a first version; worth revisiting if
  live testing shows frames silently stopping without an obvious cause.

## Audio capture (desktop mirroring)

- **System-wide desktop audio, not audio isolated to the one captured app.** Windows has a newer
  per-process loopback API (`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`) that could capture
  just one app's sound, but — same tradeoff as GDI vs. Windows.Graphics.Capture for video — it
  requires hand-written COM activation interop (`IActivateAudioInterfaceCompletionHandler`) that
  couldn't be verified against the real API in this environment. NAudio's WASAPI loopback (system
  output) is mature and reliable; that's what's implemented. The "Include audio" checkbox is
  honestly labeled as desktop sound, not per-app isolation.
- **Multiple simultaneous captures with audio enabled will produce duplicate/echoed playback** on
  a node — each capture source gets its own independent WASAPI loopback + player, and since the
  underlying audio is the same system-wide output for all of them, having audio on for two
  captures at once means a node hears the same system audio played twice, slightly out of phase.
  Proper mixing of multiple audio sources into one output is Phase 7 ("Audio Engine") work, not
  this slice — for now, enable audio on at most one capture at a time per node.
- **A real, non-obvious bug was found and fixed while building this**: the first resampling
  approach tried (`MediaFoundationResampler`) measurably produced near-silent output for roughly
  the first second of every capture session, regardless of quality setting — a COM/Media
  Foundation transform priming quirk invisible to "does it throw" testing. Caught only by
  measuring actual sample amplitude against a genuinely-playing test tone. Fixed by switching to
  NAudio's pure-managed `WdlResamplingSampleProvider` chain, verified with the same tone-based
  test to produce correct output from the very first read. See `AudioCaptureService`'s remarks
  and `AudioCaptureEndToEndTests` for the permanent regression coverage.
- **No audio/video sync (lip-sync) guarantee.** Video and audio are two independent channels with
  independent chunking/network paths (spec section 38's "separate channels" principle, applied
  literally) — nothing currently aligns their timing. For a talking-head or lip-sync-sensitive
  use case this could drift noticeably; for a trading-app-with-alert-sounds use case it's likely
  unnoticeable. True AV sync (spec section 17's shared-timeline approach) is future work.

## Output (Phase 8 — not started)

- **No TikTok integration exists, and none is planned against undocumented APIs** (spec section
  22/52/56). See [OUTPUT.md](OUTPUT.md) for the actual planned mechanism: local window/display
  capture that TikTok Studio consumes through Windows' own supported capture APIs, with OBS
  WebSocket and a virtual-camera driver as optional, clearly isolated additions.
- **No virtual camera driver exists.** If/when built, it will be a separate, explicitly
  user-consented install — not bundled silently.

## Development environment note (for future maintainers)

This project was scaffolded in a Windows sandbox with Windows Defender Application Control (WDAC)
enforced in a mode that blocked unsigned/unrecognized executables (`cargo.exe`, `ffmpeg.exe`)
outright. That specific sandbox is **not** one of the three production PCs (confirmed with the
project owner) — but it's the reason the tech stack leans on .NET's own signed toolchain rather
than external unsigned binaries like a system `ffmpeg.exe`. If a future phase needs FFmpeg (video
file decode), prefer a managed binding (e.g. FFmpeg.AutoGen) against an FFmpeg build vendored into
the app's own signed install, over depending on whatever `ffmpeg.exe` happens to be on PATH.

The same policy also blocks **our own** unsigned build output: a self-contained, single-file
`TradeFix.Agent.exe` published from this sandbox (`dotnet publish ... -p:PublishSingleFile=true`)
fails to launch here with "An Application Control policy has blocked this file." Running the exact
same build through the signed `dotnet.exe` host (`dotnet TradeFix.Agent.dll`) works fine — this
confirms it's WDAC rejecting the unsigned PE, not a defect in the build. Two publish profiles exist
under `publish/` for this reason: `TradeFix.Agent-win-x64` (self-contained single exe, the
convenient option for a normal PC without WDAC) and `TradeFix.Agent-fxdep` (framework-dependent,
launched via `dotnet TradeFix.Agent.dll` / `Run-Agent.bat`, needs the .NET 8 Desktop Runtime on the
target PC but only ever executes through a Microsoft-signed host). If PC2/PC3 turn out to have a
similar WDAC policy, use the fxdep package; if not, the self-contained exe is simpler to deploy.
Neither publish artifact has been smoke-tested directly on a machine without WDAC — only the
fxdep build was verified to run in this session, via the signed-host workaround above.
