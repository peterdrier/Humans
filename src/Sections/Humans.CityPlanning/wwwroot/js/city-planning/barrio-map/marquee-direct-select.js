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
    const freshFeature = mode.getFeature(state.featureId);
    if (freshFeature?.type !== 'Polygon' && freshFeature?.type !== 'MultiPolygon') return;
    const coordinates = freshFeature.getCoordinates();
    const multi = freshFeature.type === 'MultiPolygon';
    const polygons = multi ? coordinates : [coordinates];
    const featureId = state.featureId;
    const newSelected = [];

    polygons.forEach((rings, polygonIndex) => {
        rings.forEach((ring, ringIndex) => {
            // The public accessor closes rings; the duplicate has no separate vertex handle.
            for (let i = 0; i < ring.length - 1; i++) {
                const pt = mode.map.project(ring[i]);
                if (pt.x >= rect.minX && pt.x <= rect.maxX && pt.y >= rect.minY && pt.y <= rect.maxY) {
                    const path = multi ? `${polygonIndex}.${ringIndex}.${i}` : `${ringIndex}.${i}`;
                    newSelected.push({ feature_id: featureId, coord_path: path });
                }
            }
        });
    });

    let combined = state.marqueeShift
        ? [...(state.selectedCoordPaths ?? []).map(p => ({ feature_id: featureId, coord_path: p })), ...newSelected]
        : newSelected;

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
        if (e.originalEvent.button !== 0) return;
        // Defensive: clear any orphaned marquee element from a drag released outside the map
        if (state.marqueeEl) { state.marqueeEl.remove(); state.marqueeEl = null; }
        const meta = e.featureTarget?.properties?.meta;
        if (meta === 'vertex' || meta === 'midpoint' || meta === 'feature') {
            return DirectSelectMode.onMouseDown.call(this, state, e);
        }
        state.marqueeStart = e.point;   // container-relative pixels
        state.marqueeShift = e.originalEvent.shiftKey;
        state.isMarquee    = false;
        state.marqueeEl    = null;
        this.map.dragPan.disable();

        // MapboxDraw only dispatches mouseup on the canvas. Catch releases outside
        // the map so dragPan is re-enabled and the marquee element doesn't leak.
        const map = this.map;
        const onWindowMouseUp = () => {
            document.removeEventListener('mouseup', onWindowMouseUp);
            if (!state.marqueeStart) return; // onMouseUp already ran
            map.dragPan.enable();
            if (state.marqueeEl) { state.marqueeEl.remove(); state.marqueeEl = null; }
            state.marqueeStart = null;
            state.isMarquee    = false;
        };
        document.addEventListener('mouseup', onWindowMouseUp);
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
            state.marqueeEl?.remove();
        } else {
            // Near-zero drag = click: preserve existing click-on-empty behaviour
            DirectSelectMode.onMouseUp.call(this, state, e);
        }

        state.marqueeStart = null;
        state.isMarquee    = false;
        state.marqueeEl    = null;
    },

    onTrash(state) {
        const feature = state.feature;
        if (feature.type === 'Polygon' || feature.type === 'MultiPolygon') {
            const multi = feature.type === 'MultiPolygon';
            const coordinates = feature.getCoordinates();
            const polygons = multi ? coordinates : [coordinates];
            for (let i = 0; i < polygons.length; i++) {
                const prefix = multi ? `${i}.0.` : '0.';
                const removed = state.selectedCoordPaths.filter(path => path.startsWith(prefix)).length;
                if (polygons[i][0].length - 1 - removed < 3) return;
            }
        }
        return DirectSelectMode.onTrash.call(this, state);
    },

    onStop(state) {
        // Guard: clean up if user exits edit mode mid-drag
        this.map.dragPan.enable();
        if (state.marqueeEl) { state.marqueeEl.remove(); state.marqueeEl = null; }
        return DirectSelectMode.onStop.call(this, state);
    },
};
