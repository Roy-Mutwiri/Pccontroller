# Output Integration

**Status: design only — implemented in Phase 8.** `OutputSettings` exists in `TradeFix.Shared` as
a schema placeholder; nothing in this document is wired up yet. Documented now (per spec section
56) so the eventual design doesn't get invented ad hoc under deadline pressure.

## Constraint

TikTok LIVE / TikTok Studio has no documented, supported API for programmatic scene injection.
Spec section 22/52 is explicit: **do not build against undocumented TikTok APIs, do not fake
integration**. TradeFix's job ends at producing a clean local render each PC can feed into its
*own* copy of TikTok Studio through a mechanism Windows actually supports.

## Planned mechanisms, in order of preference

1. **Windows Graphics Capture / window capture of the TradeFix render surface.** TradeFix renders
   its scene into a real on-screen (or borderless) window; TikTok Studio (or OBS) captures that
   window via its own standard "Window Capture" or "Display Capture" source. Zero custom driver
   code, uses only capture mechanisms those apps already support. Primary path.
2. **OBS WebSocket integration (optional, spec section 23).** If OBS is installed and running
   locally, TradeFix can detect it and drive scene switching / stream start-stop through the
   [obs-websocket](https://github.com/obsproject/obs-websocket) protocol via a maintained .NET
   client. This is additive, not a dependency of the core renderer — TradeFix's own rendering
   pipeline must stand on its own without OBS installed.
3. **Windows virtual camera output.** Exposing TradeFix's render as a selectable camera device to
   *any* app (TikTok Studio, OBS, Zoom, etc.) requires installing a virtual camera driver
   component (e.g. via the Windows `IMFVirtualCamera` API introduced in Windows 11, or a
   registered DirectShow/Media Foundation source). This is real, but it is a distinct,
   separately-installed and clearly isolated component (spec section 22's explicit requirement) —
   it will not be silently bundled into the core Master/Agent install.

## What will NOT be built

- No undocumented TikTok endpoint calls.
- No fake "Start TikTok Stream" button that doesn't actually do the documented Windows capture
  handoff.
- No virtual-camera driver installed without clear, separate user consent and isolation from the
  core app (spec section 22/29).

## Per-PC independence

Each PC's output is local: PC1's render feeds PC1's local TikTok Studio, PC2's render feeds PC2's,
PC3's feeds PC3's. There is no video/audio routing between PCs — only the structured state that
lets each PC reproduce the same production independently (see [ARCHITECTURE.md](ARCHITECTURE.md)).
