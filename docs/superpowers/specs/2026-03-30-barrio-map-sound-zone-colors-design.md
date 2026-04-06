# Barrio Map: Sound Zone Color Coding

**Date:** 2026-03-30
**Branch:** barrio-map

## Overview

Color each camp polygon on the Barrio Map according to the camp's sound zone preference (`CampSeason.SoundZone`). Null preference renders in neutral gray; Surprise renders as a rainbow-stripe canvas pattern.

## Color Mapping

| SoundZone value | Fill color | Border color |
|-----------------|------------|--------------|
| Blue (0)        | `#3388ff`  | `#2266cc`    |
| Green (1)       | `#33cc55`  | `#229944`    |
| Yellow (2)      | `#ffcc00`  | `#cc9900`    |
| Orange (3)      | `#ff8800`  | `#cc6600`    |
| Red (4)         | `#ff3333`  | `#cc1111`    |
| Surprise (5)    | pattern    | `#cc00cc`    |
| null (no pref)  | `#999999`  | `#666666`    |

Own camp: `fill-opacity` 0.55 (vs 0.35 for others), `line-width` 4 (vs 2 for others).

## Data Layer Changes

### `CampPolygonDto` (ICampMapService.cs)
Add `SoundZone? SoundZone` parameter to the record.

### `CampMapService.GetPolygonsAsync`
Project `p.CampSeason.SoundZone` into the DTO.

### GeoJSON feature properties
Include `soundZone` as an integer (-1 for null, otherwise the enum integer value) so MapLibre expressions can match on it.

### SignalR `PolygonUpdated`
Add `soundZone` (int) as a fourth argument so live polygon updates carry color information without requiring a full state reload.

## Rendering Changes (Index.cshtml)

### Rainbow pattern
Before `renderMap()` runs (after map `load`), generate a small `<canvas>` (e.g. 60×60 px) with diagonal rainbow stripes and register it with `map.addImage('rainbow-pattern', canvas)`.

Stripe colors (evenly spaced diagonal bands): red → orange → yellow → green → blue → violet.

### Layers
- **`polygons-fill`**: `fill-color` uses a MapLibre `match` expression on `['get', 'soundZone']`. Surprise (5) gets white/transparent placeholder (overridden by pattern layer). `fill-opacity` uses a `case` on `isOwn`.
- **`polygons-fill-surprise`**: separate `fill` layer with `filter: ['==', ['get', 'soundZone'], 5]` and `fill-pattern: 'rainbow-pattern'`, `fill-opacity` via `case` on `isOwn`.
- **`polygons-outline`**: `line-color` via `match` on `soundZone`. `line-width` via `case` on `isOwn` (4 vs 2).

Layer order: `polygons-fill` → `polygons-fill-surprise` → `polygons-outline` → `polygons-labels`.

### SignalR refresh
Update `PolygonUpdated` handler to pass through `soundZone` when pushing updated polygon into `state.polygons` and rebuilding features for `setData`.

## What Is Not Changing
- No legend (deferred)
- No animation for Surprise (static stripe pattern)
- No changes to edit/draw mode colors (MapboxDraw styling unchanged)
- No changes to the history panel or admin page
