// Entry point: map init, draw setup, overlay sources, and button handlers.
import { appState } from './state.js';
import { CONFIG } from './config.js';
import { parseLimitZoneGeom } from './geometry.js';
import { DRAW_STYLES, generateRainbowPattern, generateCrosshatchPattern, generateDashedHorizontalPattern, renderMap } from './layers.js';
import {
    onCampPolygonClick, exitEditMode, onDrawChange, onDrawDelete,
    setEditingControlsVisible, updateAddMyBarrioVisibility,
} from './edit.js';
import { initSignalR } from './signalr.js';

async function init() {
    appState.map = new maplibregl.Map({
        container: 'map',
        style: {
            version: 8,
            sources: { esri: { type: 'raster', tiles: [CONFIG.ESRI_TILES], tileSize: 256, attribution: '© Esri' } },
            layers: [{ id: 'esri-layer', type: 'raster', source: 'esri' }],
        },
        bounds: CONFIG.MAP_BOUNDS,
    });

    appState.draw = new MapboxDraw({ displayControlsDefault: false, styles: DRAW_STYLES });
    appState.map.addControl(appState.draw);

    appState.map.on('draw.create', onDrawChange);
    appState.map.on('draw.update', onDrawChange);
    appState.map.on('draw.render', onDrawChange);
    appState.map.on('draw.delete', onDrawDelete);

    await new Promise(resolve => appState.map.on('load', resolve));

    const { map } = appState;
    map.addImage('rainbow-pattern',        generateRainbowPattern());
    map.addImage('error-stripe-pattern',   generateCrosshatchPattern('#ff2222'));
    map.addImage('overlap-stripe-pattern', generateDashedHorizontalPattern('#ff8800'));

    map.addSource('draw-label', { type: 'geojson', data: { type: 'FeatureCollection', features: [] } });
    map.addLayer({
        id: 'draw-label', type: 'symbol', source: 'draw-label',
        layout: { 'text-field': ['get', 'label'], 'text-size': 13, 'text-anchor': 'center', 'text-allow-overlap': true },
        paint: { 'text-color': '#000000', 'text-halo-color': '#ffffff', 'text-halo-width': 2 },
    });

    map.addSource('draw-edge-labels', { type: 'geojson', data: { type: 'FeatureCollection', features: [] } });
    map.addLayer({
        id: 'draw-edge-labels', type: 'symbol', source: 'draw-edge-labels',
        layout: { 'text-field': ['get', 'label'], 'text-size': 11, 'text-anchor': 'center', 'text-allow-overlap': true },
        paint: { 'text-color': '#444444', 'text-halo-color': '#ffffff', 'text-halo-width': 2 },
    });

    map.addSource('draw-warning-error', { type: 'geojson', data: { type: 'FeatureCollection', features: [] } });
    map.addLayer({
        id: 'draw-warning-error', type: 'fill', source: 'draw-warning-error',
        paint: { 'fill-pattern': 'error-stripe-pattern' },
    });

    map.addSource('draw-warning-overlap', { type: 'geojson', data: { type: 'FeatureCollection', features: [] } });
    map.addLayer({
        id: 'draw-warning-overlap', type: 'fill', source: 'draw-warning-overlap',
        paint: { 'fill-pattern': 'overlap-stripe-pattern' },
    });

    appState.campMap = await (await fetch('/api/city-planning/state')).json();
    appState.limitZoneGeom = parseLimitZoneGeom(appState.campMap.limitZoneGeoJson);
    renderMap(onCampPolygonClick);
    updateAddMyBarrioVisibility();
    initSignalR();
}

// Global keydown: Delete/Backspace for vertex deletion when draw doesn't have focus
document.addEventListener('keydown', e => {
    if ((e.key === 'Delete' || e.key === 'Backspace') && appState.activeCampSeasonId) {
        e.preventDefault();
        const poly = appState.draw.getAll().features.find(f => f.geometry.type === 'Polygon');
        if (poly && poly.geometry.coordinates[0].length <= 4) return;
        appState.draw.trash();
    }
});

// --- Button handlers ---

document.getElementById('add-my-barrio-btn')?.addEventListener('click', () => {
    appState.activeCampSeasonId = CONFIG.USER_CAMP_SEASON_ID;
    appState.draw.deleteAll();
    appState.draw.changeMode('draw_polygon');
    setEditingControlsVisible(true);
});

document.getElementById('add-barrio-select')?.addEventListener('change', e => {
    const val = e.target.value;
    if (!val) return;
    appState.activeCampSeasonId = val;
    appState.draw.deleteAll();
    appState.draw.changeMode('draw_polygon');
    e.target.value = '';
    setEditingControlsVisible(true);
});

document.getElementById('save-btn')?.addEventListener('click', async () => {
    if (!appState.activeCampSeasonId) return;
    const features = appState.draw.getAll().features;
    if (!features.length) return;

    const feature = features[0];
    const areaSqm = turf.area(feature);
    const token = document.querySelector('input[name="__RequestVerificationToken"]').value;

    const resp = await fetch(`/api/city-planning/camp-polygons/${appState.activeCampSeasonId}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
        body: JSON.stringify({ geoJson: JSON.stringify(feature), areaSqm }),
    });

    if (resp.ok) {
        exitEditMode();
        // SignalR CampPolygonUpdated will refresh the map layer
    } else {
        alert('Failed to save polygon. Please try again.');
    }
});

document.getElementById('cancel-btn')?.addEventListener('click', () => {
    if (!confirm('Discard unsaved changes?')) return;
    exitEditMode();
});

init();
