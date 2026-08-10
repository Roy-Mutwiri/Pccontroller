# Source System

Schema implemented in `src/TradeFix.Shared/Models` and `Enums` (Phase 0). Rendering and the
Add-Source UI are Phase 3/4 work — this document describes the data model that phase builds on.

## Source types

`TradeFix.Shared.Enums.SourceType`: `Camera`, `Microphone`, `DisplayCapture`, `WindowCapture`,
`Browser`, `Image`, `Video`, `AudioFile`, `Text`, `Background`, `WebContent`, `LocalMedia`,
`Placeholder`, `Group`.

## Logical vs. device-mapped (spec section 6)

Every `SourceDefinition` derives a `Category`:

- **Logical** (`Image`, `Video`, `Text`, `Browser`, `Background`, `WebContent`, `AudioFile`,
  `LocalMedia`, `Placeholder`, `Group`): the definition is reproduced *identically* on every
  node. An Image source's file hash, a Browser source's URL, a Text source's string — all travel
  verbatim to PC2 and PC3.
- **DeviceMapped** (`Camera`, `Microphone`, `DisplayCapture`, `WindowCapture`): the logical
  definition (e.g. "Main Camera") is shared, but each node resolves it to a *local* physical
  device or window via a separate `DeviceMapping` record (`LogicalSourceId`, `NodeId`,
  `DeviceType`, `DeviceIdentifier`, `DeviceDisplayName`). PC1's "Main Camera" can point at a
  Logitech webcam while PC2's points at an Elgato capture card — the production stays identical,
  the hardware binding doesn't have to.

This distinction is a computed property (`SourceDefinition.Category`), not something the author
sets by hand, so it can never drift out of sync with the source's actual type.

## Transform

`Transform2D`: `X`, `Y`, `Width`, `Height`, `RotationDegrees`, `ScaleX`/`ScaleY`, `Opacity`,
`ZIndex`, `Visible`, `Crop` (`CropBox`: `Left`/`Top`/`Right`/`Bottom`). Applies to every visual
source uniformly (spec section 14).

## Per-node overrides (spec section 40)

`SourceDefinition.NodeOverrides` is a `NodeId → NodeOverride` map. A `NodeOverride` can carry a
replacement `Transform2D` and/or a replacement type-specific `Config` payload. Absence of an
override for a node means "follow the global definition" — divergence is always explicit, never
accidental, matching the spec's "do not accidentally create divergence" requirement.

## Scenes

A `SceneDefinition` is an ordered list of `SceneSourceRef` (source id + layer/z-index within that
scene + optional scene-local transform tweak). A source can appear in multiple scenes with
different placements without duplicating its definition.

## Groups

`SourceType.Group` plus `SourceDefinition.GroupId` on member sources gives the data model for
spec section 15 (move a group as a unit). Group-aware transform propagation is Phase 4+ logic, not
yet implemented.

## Filters and plugin sources (spec section 45)

`SourceFilter` (`Id`, `Type`, `Config` as `JsonElement`, `Enabled`) is defined now so the schema
doesn't need a breaking change when filters ship. The source *plugin* interface
(`create/destroy/render/update/serialize/deserialize/validate`) lives in `TradeFix.Sources` and is
Phase 4 work — the project is intentionally not committing to that interface's exact shape before
a first real source (Image) is implemented against it.

## What's NOT implemented yet

Nothing in this document is rendered yet — Phase 1 only implements node connectivity. Adding a
source in a future Master UI, transmitting it, and rendering it locally on each node is Phase 2–4.
