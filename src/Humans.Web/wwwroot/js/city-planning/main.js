// Read-only overview map for CityPlanning/Index.
// Shows official zones and camps always; containers and camp limits are togglable.

import { CONFIG } from './config.js';
import { initMeasure, enterMeasureMode, exitMeasureMode, isMeasuring } from '../container-map/measure.js';

const LAYER_GROUPS = {
    containers:  ['containers-fill', 'containers-active-fill', 'containers-labels'],
    campLimits:  [], // populated after limit zone is added
};

async function init() {
    const map = new maplibregl.Map({
        container: 'map',
        style: {
            version: 8,
            sources: { esri: { type: 'raster', tiles: [CONFIG.ESRI_TILES], tileSize: 256, maxzoom: 19, attribution: '© Esri' } },
            layers:  [{ id: 'esri-layer', type: 'raster', source: 'esri' }],
        },
        bounds: CONFIG.MAP_BOUNDS,
    });

    await new Promise(resolve => map.on('load', resolve));
    initMeasure(map);

    const state = await fetch('/api/city-planning/state').then(r => r.json());

    // ── Official zones (always visible) ──────────────────────────────────────
    if (state.officialZonesGeoJson) {
        map.addSource('official-zones', { type: 'geojson', data: JSON.parse(state.officialZonesGeoJson) });
        map.addLayer({ id: 'official-zones-fill', type: 'fill', source: 'official-zones', paint: { 'fill-color': '#555555', 'fill-opacity': 0.12 } });
        map.addLayer({ id: 'official-zones-line', type: 'line', source: 'official-zones', paint: { 'line-color': '#555555', 'line-width': 1.5 } });
        map.addLayer({
            id: 'official-zones-labels', type: 'symbol', source: 'official-zones',
            layout: { 'text-field': ['get', 'name'], 'text-size': 12, 'text-anchor': 'center', 'text-allow-overlap': false },
            paint: { 'text-color': '#333333', 'text-halo-color': '#ffffff', 'text-halo-width': 2 },
        });
    }

    // ── Camps (always visible) ────────────────────────────────────────────────
    const campFeatures = (state.campPolygons ?? [])
        .filter(p => p.geoJson)
        .map(p => {
            const f = JSON.parse(p.geoJson);
            if (!f.properties) f.properties = {};
            f.properties.campName  = p.campName;
            f.properties.soundZone = p.soundZone;
            return f;
        });

    map.addSource('camp-polygons', { type: 'geojson', data: { type: 'FeatureCollection', features: campFeatures } });
    map.addLayer({
        id: 'camp-polygons-fill', type: 'fill', source: 'camp-polygons',
        paint: {
            'fill-color': ['match', ['get', 'soundZone'],
                0, '#88aadd', 1, '#88bb88', 2, '#ddcc66', 3, '#ddaa66', 4, '#dd8888', '#aaaaaa',
            ],
            'fill-opacity': 0.25,
        },
    });
    map.addLayer({
        id: 'camp-polygons-outline', type: 'line', source: 'camp-polygons',
        paint: {
            'line-color': ['match', ['get', 'soundZone'],
                0, '#2266cc', 1, '#229944', 2, '#cc9900', 3, '#cc6600', 4, '#cc1111', '#666666',
            ],
            'line-width': 1.5,
        },
    });
    map.addLayer({
        id: 'camp-polygons-labels', type: 'symbol', source: 'camp-polygons',
        layout: { 'text-field': ['get', 'campName'], 'text-size': 12, 'text-anchor': 'center', 'text-allow-overlap': false },
        paint: { 'text-color': '#000000', 'text-halo-color': '#ffffff', 'text-halo-width': 2 },
    });

    // ── Camp limits (togglable, on by default) ───────────────────────────────
    if (state.limitZoneGeoJson) {
        let limitData = JSON.parse(state.limitZoneGeoJson);
        if (limitData.type === 'Feature') limitData = { type: 'FeatureCollection', features: [limitData] };
        map.addSource('limit-zone', { type: 'geojson', data: limitData });
        map.addLayer({ id: 'limit-zone-fill', type: 'fill', source: 'limit-zone', layout: { visibility: 'none' }, paint: { 'fill-color': '#ffffff', 'fill-opacity': 0.06 } });
        map.addLayer({ id: 'limit-zone-line', type: 'line', source: 'limit-zone', layout: { visibility: 'none' }, paint: { 'line-color': '#ffffff', 'line-width': 2, 'line-dasharray': [4, 2] } });
        LAYER_GROUPS.campLimits = ['limit-zone-fill', 'limit-zone-line'];
    }

    // ── Containers (togglable, on by default) ────────────────────────────────
    const css = v => getComputedStyle(document.documentElement).getPropertyValue(v).trim();
    const containerColor = css('--container-fill') || '#8b7355';

    const containersResp = await fetch(`/api/city-planning/containers/${CONFIG.YEAR}`).then(r => r.json());
    const placedFeatures = containersResp
        .filter(c => c.locationGeoJson)
        .map(c => {
            const f = JSON.parse(c.locationGeoJson);
            if (!f.properties) f.properties = {};
            f.properties.name = c.name;
            return f;
        });

    map.addSource('containers', { type: 'geojson', data: { type: 'FeatureCollection', features: placedFeatures } });
    map.addLayer({ id: 'containers-fill', type: 'fill', source: 'containers', layout: { visibility: 'none' }, paint: { 'fill-color': containerColor, 'fill-opacity': 0.7 } });
    map.addLayer({
        id: 'containers-labels', type: 'symbol', source: 'containers',
        minzoom: 17,
        layout: { visibility: 'none', 'text-field': ['get', 'name'], 'text-size': 10, 'text-anchor': 'center', 'text-allow-overlap': false },
        paint: { 'text-color': '#ffffff', 'text-halo-color': '#000000', 'text-halo-width': 1 },
    });

    // ── Toggle buttons ────────────────────────────────────────────────────────
    wireToggle('toggle-containers',  LAYER_GROUPS.containers,  map);
    wireToggle('toggle-camp-limits', LAYER_GROUPS.campLimits,  map);

    document.getElementById('measure-btn').addEventListener('click', () => {
        if (isMeasuring()) exitMeasureMode(); else enterMeasureMode();
    });
}

function wireToggle(btnId, layerIds, map) {
    const btn = document.getElementById(btnId);
    if (!btn || layerIds.length === 0) {
        if (btn) btn.disabled = true;
        return;
    }
    let visible = false;
    btn.addEventListener('click', () => {
        visible = !visible;
        const v = visible ? 'visible' : 'none';
        layerIds.forEach(id => { if (map.getLayer(id)) map.setLayoutProperty(id, 'visibility', v); });
        btn.classList.toggle('active', visible);
        btn.setAttribute('aria-pressed', String(visible));
    });
}

init().catch(err => console.error('Overview map init failed:', err));
