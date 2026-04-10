// Editing mode: toolbar state, draw label updates, popup, button handlers.
import { appState } from './state.js';
import { CONFIG } from './config.js';
import { isOutsideZone, overlapsOtherCamps } from './geometry.js';
import { setActivePolygonDim } from './layers.js';

function escHtml(s) {
    const d = document.createElement('div');
    d.textContent = s;
    return d.innerHTML;
}

// --- Popup ---

export function onCampPolygonClick(e) {
    if (appState.activeCampSeasonId) return;
    const props = e.features[0].properties;
    const campSeasonId = props.campSeasonId;
    const isOwn = props.campSeasonId === CONFIG.USER_CAMP_SEASON_ID;
    const canEdit = CONFIG.IS_MAP_ADMIN || (CONFIG.IS_PLACEMENT_OPEN && isOwn);

    const area         = props.areaSqm   ? `<div class="text-muted small">${Math.round(props.areaSqm).toLocaleString()} m²</div>` : '';
    const warning      = props.outsideZone ? `<div class="text-danger small">⚠️ Outside limits</div>` : '';
    const overlapWarn  = props.overlaps    ? `<div class="text-warning small">⚠️ Overlaps with another barrio</div>` : '';
    const editBtn      = canEdit ? `<button class="btn btn-primary btn-sm js-edit-barrio-btn">Edit</button>` : '';
    const historyBtn   = `<button class="btn btn-outline-secondary btn-sm js-history-barrio-btn"><i class="fa fa-history me-1"></i>History</button>`;

    if (appState.currentPopup) appState.currentPopup.remove();
    appState.currentPopup = new maplibregl.Popup().setLngLat(e.lngLat)
        .setHTML(`<div><strong>${escHtml(props.campName || 'Camp')}</strong></div>${area}${warning}${overlapWarn}<div class="d-flex flex-column gap-1 mt-1">${editBtn}${historyBtn}</div>`)
        .addTo(appState.map);

    if (canEdit) {
        appState.currentPopup.getElement().querySelector('.js-edit-barrio-btn')
            .addEventListener('click', () => startEditing(campSeasonId));
    }
    appState.currentPopup.getElement().querySelector('.js-history-barrio-btn')
        .addEventListener('click', () => loadHistory(campSeasonId, canEdit));
}

// --- Edit mode lifecycle ---

export function startEditing(campSeasonId) {
    if (appState.currentPopup) { appState.currentPopup.remove(); appState.currentPopup = null; }

    appState.previewCampSeasonId = null;
    appState.activeCampSeasonId = campSeasonId;
    setActivePolygonDim(campSeasonId);
    appState.draw.deleteAll();

    const poly = appState.campMap.campPolygons.find(p => p.campSeasonId === campSeasonId);
    if (poly) {
        const f = JSON.parse(poly.geoJson);
        if (!f.id) f.id = 'active-polygon';
        appState.draw.add(f);
        appState.draw.changeMode('direct_select', { featureId: f.id });
    }

    setEditingControlsVisible(true);
    updateSaveButton();
}

export function exitEditMode() {
    appState.draw.deleteAll();
    setActivePolygonDim(null);
    appState.activeCampSeasonId = null;
    document.getElementById('save-btn').disabled = true;
    const cancelBtn = document.getElementById('cancel-btn');
    if (cancelBtn) cancelBtn.style.display = 'none';
    clearDrawLabel();
    setEditingControlsVisible(false);
}

// --- Draw event handlers ---

export function onDrawChange() { updateSaveButton(); }

export function onDrawDelete() {
    setActivePolygonDim(null);
    appState.activeCampSeasonId = null;
    document.getElementById('save-btn').disabled = true;
    const cancelBtn = document.getElementById('cancel-btn');
    if (cancelBtn) cancelBtn.style.display = 'none';
    clearDrawLabel();
    setEditingControlsVisible(false);
}

// --- Toolbar state ---

export function setEditingControlsVisible(visible) {
    const toolbar = document.getElementById('main-toolbar');
    if (!toolbar) return;
    const saveBtn = document.getElementById('save-btn');
    if (visible) {
        toolbar.style.display = '';
        const addMyBarrioBtn = document.getElementById('add-my-barrio-btn');
        if (addMyBarrioBtn) addMyBarrioBtn.style.display = 'none';
        if (saveBtn) saveBtn.style.display = '';
        return;
    }
    if (saveBtn) saveBtn.style.display = 'none';
    updateAddMyBarrioVisibility();
    const addMyBarrioVisible = document.getElementById('add-my-barrio-btn')?.style.display !== 'none';
    const addBarrioPresent   = !!document.getElementById('add-barrio-container');
    if (!addMyBarrioVisible && !addBarrioPresent) toolbar.style.display = 'none';
}

export function updateAddMyBarrioVisibility() {
    const btn = document.getElementById('add-my-barrio-btn');
    if (!btn) return;
    const hasPolygon = appState.campMap.campPolygons.some(p => p.campSeasonId === CONFIG.USER_CAMP_SEASON_ID);
    const show = CONFIG.IS_PLACEMENT_OPEN && CONFIG.USER_CAMP_SEASON_ID && !hasPolygon;
    btn.style.display = show ? '' : 'none';
    if (show) {
        const toolbar = document.getElementById('main-toolbar');
        if (toolbar) toolbar.style.display = '';
    }
}

// --- Draw label ---

export function clearDrawLabel() {
    const { map } = appState;
    map.getSource('draw-label')?.setData({ type: 'FeatureCollection', features: [] });
    map.getSource('draw-edge-labels')?.setData({ type: 'FeatureCollection', features: [] });
    map.getSource('draw-warning-error')?.setData({ type: 'FeatureCollection', features: [] });
    map.getSource('draw-warning-overlap')?.setData({ type: 'FeatureCollection', features: [] });
}

export function updateSaveButton() {
    const { map, draw } = appState;
    const features = draw.getAll().features;
    const hasValidPolygon = features.some(f =>
        f.geometry.type === 'Polygon' && (f.geometry.coordinates[0]?.length ?? 0) >= 4);
    const editing = hasValidPolygon && appState.activeCampSeasonId;
    document.getElementById('save-btn').disabled = !editing;
    const cancelBtn = document.getElementById('cancel-btn');
    if (cancelBtn) cancelBtn.style.display = appState.activeCampSeasonId ? '' : 'none';

    if (hasValidPolygon) {
        const poly     = features[0];
        const area     = turf.area(poly);
        const centroid = turf.centroid(poly);
        const outside  = isOutsideZone(poly);
        const overlap  = overlapsOtherCamps(poly);

        map.getSource('draw-warning-error').setData(outside
            ? { type: 'FeatureCollection', features: [poly] }
            : { type: 'FeatureCollection', features: [] });
        map.getSource('draw-warning-overlap').setData(overlap
            ? { type: 'FeatureCollection', features: [poly] }
            : { type: 'FeatureCollection', features: [] });

        const warnings = [
            ...(outside  ? ['\n⚠️ Outside limits'] : []),
            ...(overlap   ? ['\n⚠️ Overlaps with another barrio'] : []),
        ];
        centroid.properties = { label: Math.round(area).toLocaleString() + ' m²' + warnings.join('') };
        map.getSource('draw-label').setData({ type: 'FeatureCollection', features: [centroid] });

        const coords = poly.geometry.coordinates[0];
        const edgeFeatures = [];
        for (let i = 0; i < coords.length - 1; i++) {
            const lengthM = turf.length(turf.lineString([coords[i], coords[i + 1]]), { units: 'meters' });
            const mid = turf.midpoint(turf.point(coords[i]), turf.point(coords[i + 1]));
            mid.properties = { label: Math.round(lengthM) + ' m' };
            edgeFeatures.push(mid);
        }
        map.getSource('draw-edge-labels').setData({ type: 'FeatureCollection', features: edgeFeatures });
    } else {
        clearDrawLabel();
    }
}

// --- History ---

export async function loadHistory(campSeasonId, canEdit = false) {
    const id = campSeasonId ?? appState.activeCampSeasonId;
    if (!id) return;

    const resp = await fetch(`/api/city-planning/camp-polygons/${id}/history`);

    if (appState.currentPopup) { appState.currentPopup.remove(); appState.currentPopup = null; }

    const campName = appState.campMap.campPolygons.find(p => p.campSeasonId === id)?.campName;
    const titleEl = document.getElementById('history-panel-title');
    if (titleEl && campName) titleEl.textContent = `History of ${campName}`;

    const list = document.getElementById('history-list');

    if (!resp.ok) {
        list.innerHTML = '<p class="text-danger text-center py-4">Failed to load history.</p>';
        bootstrap.Offcanvas.getOrCreateInstance(document.getElementById('history-panel')).show();
        return;
    }

    const history = await resp.json();
    if (!history.length) {
        list.innerHTML = '<p class="text-muted text-center py-4">No history yet.</p>';
    } else {
        list.innerHTML = history.map(h => `
            <div class="border-bottom py-2 px-1">
                <div class="d-flex justify-content-between align-items-start">
                    <div>
                        <div class="fw-semibold small">${escHtml(h.modifiedByDisplayName)}</div>
                        <div class="text-muted" style="font-size:12px">${escHtml(h.modifiedAt)} &middot; ${Math.round(h.areaSqm).toLocaleString()} m²</div>
                        <div class="text-secondary" style="font-size:12px">${escHtml(h.note)}</div>
                    </div>
                    <div class="d-flex gap-1 flex-shrink-0">
                        <button class="btn btn-outline-secondary btn-sm py-0 preview-btn" data-id="${h.id}" data-geojson="${encodeURIComponent(h.geoJson)}">Preview</button>
                        ${canEdit ? `<button class="btn btn-outline-warning btn-sm py-0 restore-btn" data-id="${h.id}">Restore</button>` : ''}
                    </div>
                </div>
            </div>
        `).join('');

        list.querySelectorAll('.preview-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                appState.previewCampSeasonId = id;
                appState.draw.deleteAll();
                appState.draw.add(JSON.parse(decodeURIComponent(btn.dataset.geojson)));
            });
        });
        list.querySelectorAll('.restore-btn').forEach(btn => {
            btn.addEventListener('click', () => restoreVersion(btn.dataset.id, id));
        });
    }

    const panel = document.getElementById('history-panel');
    panel.addEventListener('hidden.bs.offcanvas', () => {
        appState.previewCampSeasonId = null;
        if (!appState.activeCampSeasonId) appState.draw.deleteAll();
    }, { once: true });
    bootstrap.Offcanvas.getOrCreateInstance(panel).show();
}

export async function restoreVersion(historyId, campSeasonId) {
    const id = campSeasonId ?? appState.activeCampSeasonId;
    if (!id) return;
    if (!confirm('Restore this version?')) return;

    const token = document.querySelector('input[name="__RequestVerificationToken"]').value;
    const resp = await fetch(`/api/city-planning/camp-polygons/${id}/restore/${historyId}`, {
        method: 'POST',
        headers: { 'RequestVerificationToken': token },
    });
    if (resp.ok) {
        bootstrap.Offcanvas.getInstance(document.getElementById('history-panel'))?.hide();
        exitEditMode();
    } else {
        alert('Restore failed.');
    }
}
