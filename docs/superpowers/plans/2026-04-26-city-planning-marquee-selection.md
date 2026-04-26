# City Planning Marquee Vertex Selection — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add drag-to-select rectangle selection of polygon vertices in the CityPlanning map editor, complementing the existing Shift+click single-vertex selection.

**Architecture:** A custom MapboxDraw mode (`MarqueeDirectSelectMode`) spreads `direct_select` and overrides four event handlers to intercept drag-on-empty-space, render a dashed rectangle overlay, and update vertex selection on release. The mode is registered at MapboxDraw initialisation time, replacing the built-in `direct_select` key — no other files change.

**Tech Stack:** Vanilla ES module JS, MapboxDraw v1.5.1 custom mode API, MapLibre GL JS v5 (`map.project()`, `map.dragPan`).

---

## File Map

| Action | Path | Responsibility |
|---|---|---|
| Create | `src/Humans.Web/wwwroot/js/city-planning/marquee-direct-select.js` | Custom MapboxDraw mode + helpers |
| Modify | `src/Humans.Web/wwwroot/js/city-planning/main.js` | Import and register the custom mode |

---

### Task 1: Create `marquee-direct-select.js`

**Files:**
- Create: `src/Humans.Web/wwwroot/js/city-planning/marquee-direct-select.js`

This file has no dependencies beyond the globally available `MapboxDraw`. It exports a single object `MarqueeDirectSelectMode`.

- [ ] **Step 1: Create the file with the complete implementation**

```js
// src/Humans.Web/wwwroot/js/city-planning/marquee-direct-select.js
const DirectSelectMode = MapboxDraw.modes.direct_select;

function createMarqueeEl(container) {
    const el = document.createElement('div');
    el.style.cssText = 'position:absolute;pointer-events:none;border:2px dashed #ffffff;background:rgba(255,255,255,0.15);box-sizing:border-box;z-index:100;';
    container.appendChild(el);
    return el;
}

function updateMarqueeEl(el, start, cur) {
    el.style.left   = Math.min(start.x, cur.x) + 'px';
    el.style.top    = Math.min(start.y, cur.y) + 'px';
    el.style.width  = Math.abs(cur.x - start.x) + 'px';
    el.style.height = Math.abs(cur.y - start.y) + 'px';
}

function selectVerticesInRect(mode, state, rect) {
    const coords    = state.feature.coordinates[0];
    const featureId = state.featureId;
    const newSelected = [];

    // Skip last coord — it's the closing duplicate of the first vertex
    for (let i = 0; i < coords.length - 1; i++) {
        const pt = mode.map.project(coords[i]);
        if (pt.x >= rect.minX && pt.x <= rect.maxX && pt.y >= rect.minY && pt.y <= rect.maxY) {
            newSelected.push({ feature_id: featureId, coord_path: `0.${i}` });
        }
    }

    let combined = state.marqueeShift
        ? [...state.selectedCoordPaths.map(p => ({ feature_id: featureId, coord_path: p })), ...newSelected]
        : newSelected;

    // Deduplicate (Shift can produce overlaps)
    const seen = new Set();
    combined = combined.filter(c => {
        if (seen.has(c.coord_path)) return false;
        seen.add(c.coord_path);
        return true;
    });

    mode.setSelectedCoordinates(combined);
    state.selectedCoordPaths = combined.map(c => c.coord_path);
}

export const MarqueeDirectSelectMode = {
    ...DirectSelectMode,

    onMouseDown(state, e) {
        const meta = e.featureTarget?.properties?.meta;
        if (meta === 'vertex' || meta === 'midpoint') {
            return DirectSelectMode.onMouseDown.call(this, state, e);
        }
        state.marqueeStart = e.point;   // container-relative pixels
        state.marqueeShift = e.originalEvent.shiftKey;
        state.isMarquee    = false;
        state.marqueeEl    = null;
        this.map.dragPan.disable();
    },

    onDrag(state, e) {
        if (!state.marqueeStart) {
            return DirectSelectMode.onDrag.call(this, state, e);
        }
        const cur = e.point;
        if (!state.isMarquee &&
            Math.abs(cur.x - state.marqueeStart.x) < 4 &&
            Math.abs(cur.y - state.marqueeStart.y) < 4) return;

        state.isMarquee = true;
        if (!state.marqueeEl) state.marqueeEl = createMarqueeEl(this.map.getContainer());
        updateMarqueeEl(state.marqueeEl, state.marqueeStart, cur);
    },

    onMouseUp(state, e) {
        if (!state.marqueeStart) {
            return DirectSelectMode.onMouseUp.call(this, state, e);
        }
        this.map.dragPan.enable();

        if (state.isMarquee) {
            const end = e.point;
            selectVerticesInRect(this, state, {
                minX: Math.min(state.marqueeStart.x, end.x),
                maxX: Math.max(state.marqueeStart.x, end.x),
                minY: Math.min(state.marqueeStart.y, end.y),
                maxY: Math.max(state.marqueeStart.y, end.y),
            });
            if (state.marqueeEl) state.marqueeEl.remove();
        } else {
            // Near-zero drag = click: preserve existing click-on-empty behaviour
            DirectSelectMode.onMouseUp.call(this, state, e);
        }

        state.marqueeStart = null;
        state.isMarquee    = false;
        state.marqueeEl    = null;
    },

    onStop(state) {
        // Guard: clean up if user exits edit mode mid-drag
        this.map.dragPan.enable();
        if (state.marqueeEl) { state.marqueeEl.remove(); state.marqueeEl = null; }
        return DirectSelectMode.onStop.call(this, state);
    },
};
```

- [ ] **Step 2: Commit**

```bash
git add src/Humans.Web/wwwroot/js/city-planning/marquee-direct-select.js
git commit -m "feat: add MarqueeDirectSelectMode for rectangle vertex selection (nobodies-collective#523)"
```

---

### Task 2: Register the custom mode in `main.js`

**Files:**
- Modify: `src/Humans.Web/wwwroot/js/city-planning/main.js`

Two changes: add the import at the top, add the `modes` option to `new MapboxDraw(...)`.

- [ ] **Step 1: Add the import**

At the top of `main.js`, after the existing imports, add:

```js
import { MarqueeDirectSelectMode } from './marquee-direct-select.js';
```

- [ ] **Step 2: Register the mode**

Change the existing `new MapboxDraw(...)` call (currently line 23) from:

```js
appState.draw = new MapboxDraw({ displayControlsDefault: false, styles: DRAW_STYLES });
```

to:

```js
appState.draw = new MapboxDraw({
    displayControlsDefault: false,
    styles: DRAW_STYLES,
    modes: { ...MapboxDraw.modes, direct_select: MarqueeDirectSelectMode },
});
```

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/wwwroot/js/city-planning/main.js
git commit -m "feat: register MarqueeDirectSelectMode as direct_select (nobodies-collective#523)"
```

---

### Task 3: Manual smoke test

Start the app and navigate to the CityPlanning map.

```bash
dotnet run --project src/Humans.Web
```

Open a browser at `https://localhost:5001/CityPlanning` (or whatever the local port is).

- [ ] **Basic marquee draw:**
  1. Click an existing polygon to open its popup, click Edit.
  2. Click and drag on empty map space — a white dashed rectangle should appear while dragging.
  3. Release — all vertices inside the rectangle should turn highlighted (selected state).

- [ ] **Replace selection (no Shift):**
  1. Shift+click a vertex to select it.
  2. Drag a marquee over a different set of vertices.
  3. Release — only the marquee vertices are selected; the previously selected vertex is deselected.

- [ ] **Additive selection (with Shift):**
  1. Shift+click one vertex to select it.
  2. Hold Shift, drag a marquee over other vertices.
  3. Release — all of the previously selected vertex plus the marquee vertices are selected.

- [ ] **Near-zero drag (click on empty space):**
  1. Click (don't drag) on empty map space.
  2. Existing MapboxDraw behaviour should be preserved: vertices deselect and mode returns to simple_select (polygon popup closed).

- [ ] **Mid-drag escape (edit mode exit):**
  1. Start a marquee drag.
  2. While still dragging, click the Cancel button.
  3. The marquee rectangle should disappear cleanly and map panning should work normally after.

- [ ] **Map panning not broken:**
  1. After any marquee selection, verify the map can still be panned by clicking and dragging normally.

- [ ] **Existing Shift+click still works:**
  1. Shift+click individual vertices — should still add/remove from selection as before.

- [ ] **Vertex drag still works:**
  1. Click and drag a vertex — it should still move correctly (marquee should not interfere).
