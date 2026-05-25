# Persistent Multi-Measurements on City Planning Maps

**Date:** 2026-05-07
**Scope:** `src/Humans.Web/wwwroot/js/city-planning/shared/measure.js` (+ minor view edits)

## Problem

The measure tool on the BarrioMap and ContainerMap is mode-exclusive and single-shot:

1. Switching to another mode (drawing barrios, placing containers) calls `exitMeasureMode()`, which **clears the measurement** from the map.
2. Only one measurement is supported at a time. Starting a third click resets the first measurement.

User feedback: measurements should **persist** when switching to other operations, and users should be able to have **multiple measurements** visible at once.

## Goals

- Measurements remain visible after the user exits measure mode (toggles off, draws a barrio, places a container, etc.).
- Users can create an arbitrary number of measurements on the same map.
- Users can delete a single measurement, or clear all of them.

## Non-Goals

- Persistence across page reloads. Measurements are session-only scratch annotations.
- Multi-segment paths (current is two-point distance only — keep it that way).
- Server-side or cross-user storage.
- Touch/mobile-specific gestures beyond what already works.

## Behavior

### Measure mode (toggle on)

- Entering measure mode does **not** clear existing measurements.
- First click on empty map: drops point A, starts rubber-band preview line + live distance label following the cursor.
- Second click on empty map: drops point B, completes the measurement (point markers, dashed line, midpoint distance label). The new measurement joins the persistent collection. Rubber-band preview clears. **Measure mode auto-exits** — to add another measurement, the user re-clicks the Measure button.
- **Right-click** (contextmenu) on an existing measurement's point or label (while in measure mode): **deletes that measurement**. The browser context menu is suppressed for that hit. Left-clicks always go through the first/second-click measurement flow — they never delete.

### Measure mode (toggle off / switch to another mode)

- Mousemove preview detaches; rubber-band line and live label clear.
- Any in-flight `_pending` first point is discarded (so a half-started measurement is not preserved).
- All **completed** measurements stay rendered on the map.
- Measurement layers become inert: clicks pass through to whatever mode is active (placing a container, extending a polygon, etc.). The orange points are visually distinct enough to not be confused with interactive features.

### Clear-all

- A new "Clear measurements" button (`clear-measurements-btn`) sits next to the existing `measure-btn`.
- Hidden by default. Visible whenever ≥1 completed measurement exists, regardless of whether measure mode is active.
- Click: removes all measurements, hides itself.

## State Model

Replace the current globals (`_firstPoint`, `_secondPoint`) with:

```js
let _measurements = []; // Array<{ id: string, a: [lng,lat], b: [lng,lat] }>
let _pending = null;    // { a: [lng,lat] } | null — in-flight first point during measure mode
```

`id` is a stable identifier (e.g. counter or `crypto.randomUUID()`) used to:
- Tag features so click hit-testing can identify which measurement to delete.
- Re-render efficiently from the array.

## Rendering

The four existing GeoJSON sources (`measure-points`, `measure-line`, `measure-preview-line`, `measure-label`) remain. They now hold **multi-feature** FeatureCollections derived from `_measurements`:

- `measure-points`: two point features per measurement (A and B), each with `properties: { measurementId }`.
- `measure-line`: one LineString per measurement, `properties: { measurementId }`.
- `measure-label`: one symbol point at midpoint per measurement, `properties: { measurementId, label }`.
- `measure-preview-line`: only ever holds the rubber-band line for `_pending` (single feature or empty).

The pending first point itself is rendered into `measure-points` as a transient feature with no `measurementId`, so it disappears when the measurement completes (and gets replaced by the two committed points) or when measure mode exits.

A single `renderMeasurements()` function rebuilds all four sources from `_measurements` + `_pending`. Call it after every state change.

## Click Handling

The `click` and `contextmenu` handlers are only attached while measure mode is active. Outside measure mode, this module does not listen for either; measurement features are inert visual overlays.

Left-click flow inside measure mode:

1. If `_pending == null`:
   - `_pending = { a: coord }`, attach mousemove for preview, render.
2. Else (`_pending.a` is set):
   - Push `{ id: newId(), a: _pending.a, b: coord }` into `_measurements`, then call `exitMeasureMode()` (which clears `_pending`, detaches mousemove, restores cursor/button), update clear-button visibility.

Right-click (contextmenu) flow inside measure mode:

- Hit-test against `measure-points` and `measure-label` layers. If any hit has a `measurementId`, suppress the browser context menu (`e.preventDefault()`), remove that measurement, re-render, update clear-button visibility. Otherwise: do nothing (let the browser show its native menu).

## Module API

Existing exports keep their names (callers don't change):

- `initMeasure(map)` — unchanged signature; sets up sources and layers.
- `enterMeasureMode()` — attaches click + contextmenu handlers, sets cursor and button styling. Does NOT clear existing measurements.
- `exitMeasureMode()` — detaches click, contextmenu, and mousemove handlers, clears `_pending` and the rubber-band preview source, restores cursor and button styling. Does NOT clear `_measurements`.
- `isMeasuring()` — unchanged.

New export:

- `clearAllMeasurements()` — empties `_measurements`, re-renders, hides the clear-all button. Safe to call regardless of mode.

## View Changes

Both `BarrioMap.cshtml` and `ContainerMap.cshtml` add a sibling button:

```html
<button id="clear-measurements-btn" type="button" class="btn btn-sm btn-outline-danger d-none" aria-label="Clear all measurements">
  <i class="fa-solid fa-trash"></i>
</button>
```

Placement: immediately after `measure-btn` in the same toolbar group. The module toggles the `d-none` class.

The respective `main.js` files wire the button:

```js
document.getElementById('clear-measurements-btn')?.addEventListener('click', () => {
    clearAllMeasurements();
});
```

(`clearAllMeasurements` imported alongside the existing measure exports.)

## Edge Cases

- **Switching modes mid-measurement** (e.g., user clicks point A, then toggles off measure mode): `_pending` is dropped, no partial measurement is left behind. Existing completed measurements stay.
- **Re-entering measure mode**: existing measurements remain; user picks up creating new ones. No state needs restoration.
- **Map navigation (pan/zoom) during a pending measurement**: rubber-band line continues to follow the cursor (mousemove handler unchanged in semantics); committed measurements re-project naturally because they're in geo coordinates.
- **Click on a measurement point that's underneath an active-mode click target** (outside measure mode): A. The click hits the active-mode handler. The measurement is purely visual.
- **Empty `_measurements` after deletion**: clear-all button hides itself.

## Testing

Manual smoke tests via the `test-site` skill against the running site:

1. Enter measure mode on BarrioMap, create three measurements. Toggle measure mode off. All three remain visible.
2. With measure mode off, switch to barrio-edit mode and draw a polygon. The three measurements remain visible; clicks on empty map go to the polygon.
3. Re-enter measure mode. Click on one measurement's label — that measurement disappears. Two remain.
4. Click "Clear measurements" — all measurements disappear, button hides.
5. Repeat (1) and (2) on ContainerMap, including placing a container near a measurement point — placement should not be blocked by the measurement.
6. Start a measurement (one click), toggle measure mode off — pending point disappears, no partial state.

No new automated tests; the module is purely client-side rendering logic with no backend involvement.

## Out-of-Scope Cleanup

`src/Humans.Web/wwwroot/js/city-planning/container-map/measure.js` is already a one-line re-export of the shared module. No change needed there.
