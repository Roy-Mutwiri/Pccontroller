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
- **Bandwidth not yet tuned for real-world links — and deliberately less so now.** The user
  explicitly asked for the highest possible quality/resolution on every node over bandwidth
  concerns, so as of 2026-08-10 captures default to JPEG quality 100 (was 70) and up to 3840px/4K
  uncapped (was 1280px) at a default 12 FPS. That's meaningfully more data per frame than before —
  fine on a real LAN, but more likely to strain a constrained upload link when nodes connect over
  Tailscale/WAN (as in this project's actual PC1↔PC2 setup) than the old, more conservative
  defaults were. Frame rate, max dimension, and JPEG quality are all still editable per-capture in
  the Properties panel (applying restarts that capture at the new settings) for anyone who
  explicitly wants to trade quality back for bandwidth on a specific source.
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
- **Audio no longer silently drifts ahead of video under network pressure (fixed 2026-08-11).**
  Video and audio are still two independent channels with independent chunking/network paths
  (spec section 38's "separate channels" principle), and `MediaHub`'s per-subscriber queue still
  drops the oldest queued frame under backpressure — but that drop used to affect the two channels
  asymmetrically: a dropped video frame just leaves a stale frame on screen a moment longer
  (visibly frozen, harmless to timing), while a dropped *audio* chunk was silently skipped and
  never replayed, so every chunk after it played back exactly one chunk-duration too early —
  compounding every time it happened. Reported by a user as "the person speaking and his voice are
  not going the same." Root-caused via `MediaHub`'s drop-oldest design plus the fact that
  `AudioCaptureService` chunks carried no timestamp at all, so nothing downstream could tell a drop
  had happened. Fixed by having each audio chunk carry the sender's cumulative captured-audio
  timeline position (`AudioChunkFraming`, an 8-byte header), and having the Agent's audio
  subscription (`AudioSyncGapFiller`) detect gaps between consecutive timestamps and insert
  matching silence before resuming playback — giving audio the same "freeze and wait" behavior a
  stale video frame already has, instead of quietly racing ahead. Gaps larger than 3 seconds are
  treated as a real pause/reconnect rather than a run of drops and are not filled (that would mean
  playing several seconds of dead air) — playback just resyncs to the new position. Verified with a
  pure-arithmetic unit suite (`AudioSyncGapFillerTests`, 7 tests covering no-gap, single-drop,
  multi-drop, oversized-gap, and reset cases) plus the existing real-tone `AudioCaptureEndToEndTests`
  updated to decode the new wire framing. This does not add full spec-section-17 shared-timeline AV
  sync (there is still no guarantee video and audio started at the *same* instant, only that audio
  no longer drifts *relative to itself* after a drop) — genuinely simultaneous start-of-stream
  alignment between the two channels remains future work.

## Browser source

- **Only fixes freezing for web content launched *through* this app.** A captured app that
  pauses/throttles its own rendering when covered (common for Chromium-based apps generally, not
  just browsers — some standalone Electron apps do this too) can only be fixed by controlling how
  that specific process is launched. The Browser source solves this for websites, since we control
  the launch; a standalone app the user already has open and picks via the regular window picker
  can't be retroactively flagged this way — there's no way to inject launch arguments into an
  already-running process.
- **Requires Chrome or Edge to be installed**, found via the Windows "App Paths" registry. No
  fallback if neither is present — the "+ Browser" button fails with a logged reason (surfaced to
  the operator via a message box) rather than silently doing nothing.
- **Each Browser source gets its own persistent, isolated browser profile** under
  `%LocalAppData%\TradeFixBroadcast\Master\BrowserProfiles\` — logins/sessions for that specific
  source survive between launches, but are not shared with the operator's regular browser (by
  design, to guarantee the anti-throttling flags actually apply) or between different Browser
  sources. Nothing currently cleans up a profile directory when its source is removed — they
  accumulate on disk over time.
- **YouTube specifically still throttles video *playback rate* while occluded, even with all
  current flags** — verified with a real Big Buck Bunny video: covering the window didn't freeze
  it completely (the earlier bug), but the video's own progress bar advanced only ~1 second across
  a 6-second covered window (roughly 1/6th real speed), while the rest of the page (layout, title,
  description) kept rendering normally the whole time. This points to YouTube's *own player*
  reducing decode/render effort for a video it believes isn't being actively watched — separate
  from, and not fixed by, `--disable-features=CalculateNativeWinOcclusion` (which fixes generic
  Chromium compositor throttling, confirmed working via `BrowserOcclusionVideoTests`). If YouTube's
  player checks something beyond simple window occlusion/focus to decide this, no browser launch
  flag can override a website's own deliberate choice. Not yet tested whether other video-heavy
  sites behave the same way, or whether primarily-non-video sites (e.g. a trading dashboard,
  probably closer to this project's actual use case) are affected at all — general page rendering
  clearly isn't.
- **The Properties/Add dialog only accepts typed http(s) URLs** — `MasterHost.AddBrowserCaptureSource`
  itself doesn't restrict the scheme (the test suite uses `file://` URLs for a fully offline,
  deterministic test page), that validation exists only in the UI layer for user-facing input.

## Installer — TradeFix.Setup.exe not yet live-verified

- **The compiled one-click installer (`TradeFix.Setup.exe`) has not been confirmed to run cleanly
  in this project's dev sandbox**, despite thorough unit testing (77/77 passing, including real
  filesystem/registry checks) and careful code review. During end-to-end testing here, published
  builds would intermittently exit 10-28 seconds after launch with no window ever shown, no
  catchable managed exception, and no consistent Windows Event Log signature — behavior most
  consistent with some form of security/behavioral scanning specific to this sandbox reacting to a
  newly-built, frequently-rebuilt, unsigned, native-code-extracting single-file exe, rather than a
  defect in the shipped code (see PROGRESS.md's installer section for the full diagnostic trail,
  including things that were ruled out). **This sandbox is explicitly confirmed to not be one of
  the actual target PCs** this software runs on (see the WDAC section below) — but until
  `TradeFix.Setup.exe` is verified on a real target machine, use
  `installer\Install-TradeFixBroadcast.bat` (the PowerShell-based installer), which *has* been
  verified end-to-end in this same sandbox, including a full install → real launch → uninstall
  cycle with all side effects checked directly (files, shortcuts, registry, running processes).
- **If you see a "Could not find Master\TradeFix.Master.exe" (or Agent) popup from the Launcher**,
  it means the install that put the Launcher there didn't finish copying Master/Agent into the
  sibling folders it expects — almost always because whatever installer was used didn't complete
  successfully. Reinstall using `Install-TradeFixBroadcast.bat` to get a known-complete install.
- **Confirmed on a real target PC (2026-08-11), not just this dev sandbox**: `TradeFix.Setup.exe`
  opened and immediately closed on PC3 after being downloaded and extracted from the GitHub
  Release zip. This is consistent with Windows' Mark-of-the-Web (the hidden `Zone.Identifier`
  alternate-data-stream every file downloaded via a browser gets tagged with) triggering
  SmartScreen/App Control to silently kill an unrecognized, unsigned exe right after launch —
  same failure class as the sandbox behavior described above, now reproduced on real end-user
  hardware. **Fix applied to `Install-TradeFixBroadcast.ps1`**: it now runs `Unblock-File`
  recursively over the whole extracted package before copying anything, and again over the
  installed copy after copying, stripping that flag before the Master/Agent/Launcher exes are
  ever launched. This doesn't fix `TradeFix.Setup.exe` itself (still recommend
  `Install-TradeFixBroadcast.bat`) — but it does mean a user hitting this on the `.exe` path has a
  documented, verified-working alternative: unblock `TradeFix.Setup.exe` by hand (right-click →
  Properties → Unblock) if they want to keep using it, or switch to the `.bat` installer, which
  now handles unblocking automatically for everything it installs.

## Output (Phase 8 — not started)

- **No TikTok integration exists, and none is planned against undocumented APIs** (spec section
  22/52/56). See [OUTPUT.md](OUTPUT.md) for the actual planned mechanism: local window/display
  capture that TikTok Studio consumes through Windows' own supported capture APIs, with OBS
  WebSocket and a virtual-camera driver as optional, clearly isolated additions.
- **No virtual camera driver exists.** If/when built, it will be a separate, explicitly
  user-consented install — not bundled silently.

## Installer / Launcher (Phase 10, partial)

- **No auto-start with Windows.** The Launcher has to be started manually (Start Menu/Desktop
  shortcut) each time — there's no option yet to have it launch automatically on sign-in.
- **Generic tray icon.** The Launcher's system tray icon is the default OS "application" icon, not
  a custom TradeFix-branded one — no `.ico` asset exists in the project yet.
- **"Switch Role" hasn't been exercised end-to-end.** Install, launch-with-a-saved-role, and
  uninstall were all genuinely tested (see PROGRESS.md) — clicking "Switch this PC to X" in the
  tray menu at runtime, which stops the current app and starts the other, has not been.
- **Not yet installed on an actual PC2/PC3** — only tested on this dev machine so far.
- **Tailscale detection is heuristic**, not authoritative: it checks for `tailscale.exe` on PATH,
  the default Program Files install location, and one registry key. A non-default Tailscale
  install location could be missed, in which case the installer would incorrectly prompt to
  install it again — harmless (the prompt is just "here's the download page"), but not perfectly
  accurate detection.

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

**Update (2026-08-10, installer testing):** a self-contained single-file `TradeFix.Launcher.exe`,
built the same way as the Agent build above, *did* launch successfully in this same sandbox when
started via the installer script — and it in turn successfully launched a self-contained
`TradeFix.Master.exe` as a real child process. This directly contradicts the earlier observation
above. Take neither result as a reliable predictor for any other machine: WDAC behavior evidently
isn't perfectly consistent even within this one sandbox across sessions/builds, for reasons not
fully understood. This is exactly why the installer itself is `.bat`/`.ps1`-based rather than a
compiled installer.exe (see PROGRESS.md's installer section) — that part doesn't depend on this
inconsistency at all, since script hosts are consistently trusted. Whether the *installed apps'*
own exes launch cleanly on a given WDAC-restricted PC remains something to verify per-machine.
