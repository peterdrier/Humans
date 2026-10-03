// Editing mode: toolbar state, draw label updates, popup, button handlers.
import { appState } from './state.js';
import { CONFIG } from './config.js';
import { isOutsideZone, overlapsOtherCamps, getSoundZoneOutOfRange, SIZE_RATIO_UPPER, SIZE_RATIO_LOWER } from './geometry.js';
import { setActivePolygonDim } from './layers.js';
import { isMeasuring } from '../shared/measure.js';

function getCampSoundZone(campSeasonId) {
    if (!campSeasonId) return -1;
    const poly = appState.campMap?.campPolygons?.find(p => p.campSeasonId === campSeasonId);
    if (poly !== undefined) return poly.soundZone ?? -1;
    const season = appState.campMap?.campSeasonsWithoutPolygon?.find(s => s.campSeasonId === campSeasonId);
    return season?.soundZone ?? -1;
}

function getSpaceRequirementSqm(campSeasonId) {
    if (!campSeasonId) return null;
    const poly = appState.campMap?.campPolygons?.find(p => p.campSeasonId === campSeasonId);
    if (poly) return poly.spaceRequirementSqm ?? null;
    const season = appState.campMap?.campSeasonsWithoutPolygon?.find(s => s.campSeasonId === campSeasonId);
    return season?.spaceRequirementSqm ?? null;
}

function escHtml(s) {
    const d = document.createElement('div');
    d.textContent = s;
    return d.innerHTML;
}

// --- Popup ---

export function onCampPolygonClick(e) {
    if (appState.activeCampSeasonId || isMeasuring()) return;
    const props = e.features[0].properties;
    const campSeasonId = props.campSeasonId;
    const isOwn = props.campSeasonId === CONFIG.USER_CAMP_SEASON_ID;
    const canEdit = CONFIG.IS_MAP_ADMIN || (CONFIG.IS_PLACEMENT_OPEN && isOwn);

    const area         = props.areaSqm   ? `<div class="text-muted small">${Math.round(props.areaSqm).toLocaleString()} m²</div>` : '';
    const warning      = props.outsideZone ? `<div class="text-danger small">⚠️ ${escHtml(CONFIG.OUTSIDE_LIMITS)}</div>` : '';
    const overlapWarn  = props.overlaps    ? `<div class="text-warning small">⚠️ ${escHtml(CONFIG.OVERLAPS_BARRIO)}</div>` : '';
    const sizeWarn     = (() => {
        if (!props.spaceRequirementSqm || !props.areaSqm) return '';
        const ratio = props.areaSqm / props.spaceRequirementSqm;
        if (ratio > SIZE_RATIO_UPPER) return `<div class="text-warning small">⚠️ ${escHtml(CONFIG.AREA_MUCH_LARGER.replace('{0}', Math.round(props.spaceRequirementSqm).toLocaleString()))}</div>`;
        if (ratio < SIZE_RATIO_LOWER) return `<div class="text-warning small">⚠️ ${escHtml(CONFIG.AREA_MUCH_SMALLER.replace('{0}', Math.round(props.spaceRequirementSqm).toLocaleString()))}</div>`;
        return '';
    })();
    const soundZoneWarn = props.soundZoneOutOfRange ? `<div class="text-warning small">⚠️ ${escHtml(CONFIG.SOUND_ZONE_MISMATCH)}</div>` : '';
    const editBtn      = canEdit ? `<button class="btn btn-primary btn-sm js-edit-barrio-btn">${escHtml(CONFIG.EDIT)}</button>` : '';
    const historyBtn   = `<button class="btn btn-outline-secondary btn-sm js-history-barrio-btn"><i class="fa fa-history me-1"></i>${escHtml(CONFIG.HISTORY)}</button>`;

    if (appState.currentPopup) appState.currentPopup.remove();
    const nameHtml = props.campSlug
        ? `<a href="/Barrios/${encodeURIComponent(props.campSlug)}" class="fw-bold text-decoration-none">${escHtml(props.campName || CONFIG.GENERIC_CAMP)}</a>`
        : `<strong>${escHtml(props.campName || CONFIG.GENERIC_CAMP)}</strong>`;
    appState.currentPopup = new maplibregl.Popup().setLngLat(e.lngLat)
        .setHTML(`<div>${nameHtml}</div>${area}${warning}${overlapWarn}${sizeWarn}${soundZoneWarn}<div class="d-flex flex-column gap-1 mt-1">${editBtn}${historyBtn}</div>`)
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
    document.getElementById('cancel-btn')?.classList.add('d-none');
    clearDrawLabel();
    setEditingControlsVisible(false);
}

// --- Draw event handlers ---

export function onDrawChange() {
    lastRenderedPolyKey = null; // force the next render-pass to run
    updateSaveButton();
}

// draw.render fires every animation frame while in any draw mode. We only need
// to recompute labels/warnings when the active polygon's coordinates actually
// changed (i.e., during vertex drag) — every other render-tick is a no-op.
let lastRenderedPolyKey = null;
export function onDrawRender() {
    const features = appState.draw.getAll().features;
    const poly = features.find(f =>
        f.geometry.type === 'Polygon' && (f.geometry.coordinates[0]?.length ?? 0) >= 4);
    const key = poly ? JSON.stringify(poly.geometry.coordinates) : '';
    if (key === lastRenderedPolyKey) return;
    lastRenderedPolyKey = key;
    updateSaveButton();
}

export function onDrawDelete() {
    setActivePolygonDim(null);
    appState.activeCampSeasonId = null;
    document.getElementById('save-btn').disabled = true;
    document.getElementById('cancel-btn')?.classList.add('d-none');
    clearDrawLabel();
    setEditingControlsVisible(false);
}

// --- Toolbar state ---

export function setEditingControlsVisible(visible) {
    const toolbar = document.getElementById('main-toolbar');
    if (!toolbar) return;
    const saveBtn = document.getElementById('save-btn');
    if (visible) {
        toolbar.classList.remove('d-none');
        document.getElementById('add-my-barrio-btn')?.classList.add('d-none');
        saveBtn?.classList.remove('d-none');
        return;
    }
    saveBtn?.classList.add('d-none');
    updateAddMyBarrioVisibility();
    const addMyBarrioVisible = document.getElementById('add-my-barrio-btn')?.classList.contains('d-none') === false;
    const addBarrioPresent   = !!document.getElementById('add-barrio-container');
    if (!addMyBarrioVisible && !addBarrioPresent) toolbar.classList.add('d-none');
}

export function updateAddMyBarrioVisibility() {
    const btn = document.getElementById('add-my-barrio-btn');
    if (!btn) return;
    const hasPolygon = appState.campMap.campPolygons.some(p => p.campSeasonId === CONFIG.USER_CAMP_SEASON_ID);
    const show = CONFIG.IS_PLACEMENT_OPEN && CONFIG.USER_CAMP_SEASON_ID && !hasPolygon;
    btn.classList.toggle('d-none', !show);
    if (show) {
        document.getElementById('main-toolbar')?.classList.remove('d-none');
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
    document.getElementById('cancel-btn')?.classList.toggle('d-none', !appState.activeCampSeasonId);

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

        const editId = appState.activeCampSeasonId ?? appState.previewCampSeasonId;
        const spaceReqSqm = getSpaceRequirementSqm(editId);
        const sizeWarning = (() => {
            if (!spaceReqSqm) return '';
            const ratio = area / spaceReqSqm;
            if (ratio > SIZE_RATIO_UPPER) return `\n⚠️ ${CONFIG.AREA_LARGER.replace('{0}', Math.round(spaceReqSqm).toLocaleString())}`;
            if (ratio < SIZE_RATIO_LOWER) return `\n⚠️ ${CONFIG.AREA_SMALLER.replace('{0}', Math.round(spaceReqSqm).toLocaleString())}`;
            return '';
        })();
        const soundZoneMismatch = getSoundZoneOutOfRange(poly, getCampSoundZone(editId));
        const warnings = [
            ...(outside           ? ["\n⚠️ " + CONFIG.OUTSIDE_LIMITS] : []),
            ...(overlap            ? ["\n⚠️ " + CONFIG.OVERLAPS_BARRIO] : []),
            ...(sizeWarning        ? [sizeWarning] : []),
            ...(soundZoneMismatch  ? ["\n⚠️ " + CONFIG.SOUND_ZONE_MISMATCH] : []),
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

let historyVersion = 0;
const historyPanel = document.getElementById('history-panel');
historyPanel.addEventListener('hide.bs.offcanvas', () => { historyVersion++; });
historyPanel.addEventListener('hidden.bs.offcanvas', () => {
    appState.previewCampSeasonId = null;
    if (!appState.activeCampSeasonId) appState.draw.deleteAll();
});

export async function loadHistory(campSeasonId, canEdit = false) {
    const id = campSeasonId ?? appState.activeCampSeasonId;
    if (!id) return;
    const version = ++historyVersion;

    if (appState.currentPopup) { appState.currentPopup.remove(); appState.currentPopup = null; }

    const campName = appState.campMap.campPolygons.find(p => p.campSeasonId === id)?.campName;
    const titleEl = document.getElementById('history-panel-title');
    if (titleEl && campName) titleEl.textContent = CONFIG.HISTORY_FOR_CAMP.replace('{0}', () => campName);

    const list = document.getElementById('history-list');
    list.innerHTML = '';

    let history;
    try {
        const resp = await fetch(`/api/city-planning/camp-polygons/${id}/history`);
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
        history = await resp.json();
    } catch (error) {
        if (version !== historyVersion) return;
        console.error('Failed to load barrio polygon history', error);
        list.innerHTML = `<p class="text-danger text-center py-4">${escHtml(CONFIG.HISTORY_LOAD_FAILED)}</p>`;
        bootstrap.Offcanvas.getOrCreateInstance(document.getElementById('history-panel')).show();
        return;
    }
    if (version !== historyVersion) return;
    if (!history.length) {
        list.innerHTML = `<p class="text-muted text-center py-4">${escHtml(CONFIG.HISTORY_EMPTY)}</p>`;
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
                        <button class="btn btn-outline-secondary btn-sm py-0 preview-btn" data-id="${h.id}" data-geojson="${encodeURIComponent(h.geoJson)}">${escHtml(CONFIG.PREVIEW)}</button>
                        ${CONFIG.IS_MAP_ADMIN ? `<button class="btn btn-outline-warning btn-sm py-0 restore-btn" data-id="${h.id}">Restore</button>` : ''}
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

    bootstrap.Offcanvas.getOrCreateInstance(historyPanel).show();
}

export async function restoreVersion(historyId, campSeasonId) {
    const id = campSeasonId ?? appState.activeCampSeasonId;
    if (!id) return;
    if (!confirm('Restore this version?')) return;

    const token = document.querySelector('input[name="__RequestVerificationToken"]').value;
    let resp;
    try {
        resp = await fetch(`/api/city-planning/camp-polygons/${id}/restore/${historyId}`, {
            method: 'POST',
            headers: { 'RequestVerificationToken': token },
        });
    } catch (error) {
        console.error('Failed to restore barrio polygon', error);
        alert('Restore failed.');
        return;
    }
    if (resp.ok) {
        bootstrap.Offcanvas.getInstance(document.getElementById('history-panel'))?.hide();
        exitEditMode();
    } else {
        alert('Restore failed.');
    }
}
