# City Planning Map — Marquee Vertex Selection

**Date:** 2026-04-26
**Feature:** Rectangular marquee selection of polygon vertices in the CityPlanning map editor

## Context

The polygon editor in CityPlanning uses MapboxDraw in `direct_select` mode. Users can already select multiple vertices with Shift+click, but this is cumbersome when vertices are dense. This feature adds drag-to-select: holding and dragging on empty map space draws a selection rectangle; releasing selects all vertices inside it.

## Behaviour

- **Trigger:** mousedown on empty map space (no vertex or midpoint under cursor) followed by a drag of more than 4px in either axis.
- **Visual feedback:** a dashed white rectangle (`border: 2px dashed #ffffff`, `background: rgba(255,255,255,0.15)`) is rendered as a `position:absolute` div inside the map container while dragging.
- **Selection on release:**
  - Without Shift: the vertices inside the rectangle become the new selection (replaces any existing selection).
  - With Shift (held at the moment of mousedown): the vertices inside the rectangle are unioned with the existing selection.
- **Near-zero drag (< 4px):** treated as a click; existing click-on-empty-space behaviour (deselect all, return to simple_select) is preserved by delegating to the parent mode.
- **Complements** existing Shift+click single-vertex selection; both methods remain available.

## Architecture

### New file: `wwwroot/js/city-planning/marquee-direct-select.js`

Exports `MarqueeDirectSelectMode`, an object that spreads `MapboxDraw.modes.direct_select` and overrides four handlers:

| Handler | Behaviour |
|---|---|
| `onMouseDown` | If target is vertex/midpoint → delegate to parent. Otherwise record `state.marqueeStart = e.point`, `state.marqueeShift = e.originalEvent.shiftKey`, disable `map.dragPan`. |
| `onDrag` | If no `state.marqueeStart` → delegate to parent (vertex/feature drag). Otherwise update the marquee div once the 4px threshold is crossed; lazily create the div on first threshold crossing. |
| `onMouseUp` | If no `state.marqueeStart` → delegate to parent. Re-enable `dragPan`. If `state.isMarquee`, compute selection and remove div. If not (click), delegate `onMouseUp` to parent. Clear all marquee state. |
| `onStop` | Re-enable `dragPan`, remove marquee div if present, delegate to parent. Guards against exiting edit mode mid-drag. |

### Modified file: `wwwroot/js/city-planning/main.js`

Import `MarqueeDirectSelectMode` and register it when initialising MapboxDraw:

```js
import { MarqueeDirectSelectMode } from './marquee-direct-select.js';

appState.draw = new MapboxDraw({
    displayControlsDefault: false,
    styles: DRAW_STYLES,
    modes: { ...MapboxDraw.modes, direct_select: MarqueeDirectSelectMode },
});
```

`edit.js` is unchanged — it already calls `draw.changeMode('direct_select', ...)`.

## Vertex Selection Logic

On mouse-up with a valid marquee rectangle:

1. Build pixel rect: `{ minX, maxX, minY, maxY }` from `state.marqueeStart` and `e.point`.
2. Iterate `state.feature.coordinates[0]`, skipping the last coordinate (closing duplicate).
3. For each coordinate at index `i`, project with `map.project([lng, lat])` → `{x, y}`.
4. If within rect, add `{ feature_id: state.featureId, coord_path: "0.i" }` to `newSelected`.
5. Apply Shift logic:
   - Shift: union `state.selectedCoordPaths.map(p => ({ feature_id, coord_path: p }))` with `newSelected`, deduplicated by `coord_path`.
   - No Shift: use `newSelected` directly.
6. Call `mode.setSelectedCoordinates(combined)` and set `state.selectedCoordPaths = combined.map(c => c.coord_path)`.

## Marquee Div

```
position: absolute
pointer-events: none
border: 2px dashed #ffffff
background: rgba(255, 255, 255, 0.15)
box-sizing: border-box
z-index: 100
```

Positioned and sized using container-relative pixel coordinates (`e.point`) on every `onDrag` call. Removed on `onMouseUp` or `onStop`.

## Out of Scope

- Deselection via marquee (drawing a rect to deselect vertices inside it).
- Support for polygons with holes (inner rings); the editor only handles the outer ring `coordinates[0]`.
- Any changes to the save/load/history flow.
