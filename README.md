# TradeFix Broadcast Control Center

A distributed broadcast control system: one PC (the **Master**) controls a live production —
scenes, sources, layout — and any number of other PCs (**Render Nodes**) mirror it live, including
captured app/screen content with audio, cursor included.

## Install (end users)

1. Get a built package — either `dist\` (just double-click `TradeFix.Setup.exe`) or `installer\` +
   `publish\` (run `installer\Install-TradeFixBroadcast.bat`). See "Build" below if you're building
   it yourself rather than downloading a pre-built one. **`Install-TradeFixBroadcast.bat` is the
   currently-recommended path** — it's been fully verified end-to-end (install, real launch,
   uninstall, all side effects checked); `TradeFix.Setup.exe` is a newer compiled installer that
   does the same thing but hasn't been confirmed to run cleanly on every machine yet — see
   [docs/KNOWN_LIMITATIONS.md](docs/KNOWN_LIMITATIONS.md#installer--tradefixsetupexe-not-yet-live-verified).
   Both produce an identical install; try the `.exe` first, fall back to the `.bat` if it doesn't
   launch as expected.
2. It installs per-user (no admin needed), creates Start Menu/Desktop shortcuts, and launches the
   app.
3. First launch asks **"What is this PC?"** — pick **Master** on the one PC that should control
   the production, and **Render Node** on every other PC. You can switch a PC's role later from
   the TradeFix Broadcast tray icon.
4. If a Render Node isn't on the same local network as the Master, install
   [Tailscale](https://tailscale.com/download/windows) on both (the installer checks and offers to
   open that page if it's missing) — see [docs/NODE_SYSTEM.md](docs/NODE_SYSTEM.md) for pairing
   details.

To remove it later: Windows Settings → Apps → "TradeFix Broadcast Control Center" → Uninstall (or
run `Uninstall-TradeFixBroadcast.bat`/`TradeFix.Setup.exe --uninstall` from the install folder).

**Seeing "Could not find Master\TradeFix.Master.exe" (or Agent) from the tray app?** That means an
earlier install didn't finish copying files — reinstall with `Install-TradeFixBroadcast.bat` to get
a known-complete install.

## Build (developers)

Requires the .NET 8 SDK.

```
dotnet build TradeFixBroadcast.sln
dotnet test TradeFixBroadcast.sln
```

To produce an installable package (self-contained builds of Master, Agent, Launcher, and the
compiled installer — no separate .NET runtime needed on the target PC):

```
installer\Build-Distributable.ps1
```

This publishes into `publish\` and assembles `dist\` (`TradeFix.Setup.exe` sitting directly next to
`publish\` — the layout an end user gets). Zip either `dist\`, or `installer\` together with
`publish\`, and copy it to another PC to install there.

## Project layout

- `src/TradeFix.Master` — the control app (scene/source editor, live preview, node management).
- `src/TradeFix.Agent` — the render node app (mirrors the Master's active scene).
- `src/TradeFix.Launcher` — the app end users actually run day-to-day; a small role-picker/tray
  supervisor that starts Master or Agent based on the chosen role.
- `src/TradeFix.Setup` — the compiled installer (`TradeFix.Setup.exe`) end users double-click once
  to install everything above.
- `src/TradeFix.Network`, `TradeFix.Protocol`, `TradeFix.Sources`, `TradeFix.Audio`, `TradeFix.Assets`,
  `TradeFix.Database`, `TradeFix.Common`, `TradeFix.Shared` — shared libraries (networking/pairing,
  wire protocol, screen/audio capture, asset storage, persistence, logging, and shared models).
- `tests/` — xUnit test projects, one per library/app, favoring real capture/audio/network over
  mocks where practical (see [docs/PROGRESS.md](docs/PROGRESS.md) for specifics).
- `docs/` — architecture, protocol, node system (pairing/networking), and an honest running log of
  what's built, what broke, and how it was fixed or is still limited
  ([docs/KNOWN_LIMITATIONS.md](docs/KNOWN_LIMITATIONS.md)).
