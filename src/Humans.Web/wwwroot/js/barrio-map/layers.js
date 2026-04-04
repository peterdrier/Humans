// Map layer definitions and rendering. Depends on maplibregl + MapboxDraw (globals).
import { appState } from './state.js';
import { buildCampPolygonFeatures } from './geometry.js';

export const DRAW_STYLES = [
    { id: 'gl-draw-polygon-fill-inactive',              type: 'fill',   filter: ['all', ['==', 'active', 'false'], ['==', '$type', 'Polygon'],    ['!=', 'mode', 'static']], paint: { 'fill-color': '#ffffff', 'fill-opacity': 0.1 } },
    { id: 'gl-draw-polygon-fill-active',                type: 'fill',   filter: ['all', ['==', 'active', 'true'],  ['==', '$type', 'Polygon']],                              paint: { 'fill-color': '#ffffff', 'fill-opacity': 0.2 } },
    { id: 'gl-draw-polygon-stroke-inactive',            type: 'line',   filter: ['all', ['==', 'active', 'false'], ['==', '$type', 'Polygon'],    ['!=', 'mode', 'static']], layout: { 'line-cap': 'round', 'line-join': 'round' }, paint: { 'line-color': '#ffffff', 'line-width': 2 } },
    { id: 'gl-draw-polygon-stroke-active',              type: 'line',   filter: ['all', ['==', 'active', 'true'],  ['==', '$type', 'Polygon']],                              layout: { 'line-cap': 'round', 'line-join': 'round' }, paint: { 'line-color': '#ffffff', 'line-width': 3 } },
    { id: 'gl-draw-line-inactive',                      type: 'line',   filter: ['all', ['==', 'active', 'false'], ['==', '$type', 'LineString'], ['!=', 'mode', 'static']], layout: { 'line-cap': 'round', 'line-join': 'round' }, paint: { 'line-color': '#ffffff', 'line-width': 2 } },
    { id: 'gl-draw-line-active',                        type: 'line',   filter: ['all', ['==', 'active', 'true'],  ['==', '$type', 'LineString']],                           layout: { 'line-cap': 'round', 'line-join': 'round' }, paint: { 'line-color': '#ffffff', 'line-dasharray': ['literal', [2, 2]], 'line-width': 3 } },
    { id: 'gl-draw-polygon-and-line-vertex-stroke-inactive', type: 'circle', filter: ['all', ['==', 'meta', 'vertex'], ['==', '$type', 'Point'], ['!=', 'mode', 'static'], ['==', 'active', 'false']], paint: { 'circle-radius': 10, 'circle-color': '#fff' } },
    { id: 'gl-draw-polygon-and-line-vertex-inactive',        type: 'circle', filter: ['all', ['==', 'meta', 'vertex'], ['==', '$type', 'Point'], ['!=', 'mode', 'static'], ['==', 'active', 'false']], paint: { 'circle-radius': 7,  'circle-color': '#0080ff' } },
    { id: 'gl-draw-polygon-and-line-vertex-stroke-active',   type: 'circle', filter: ['all', ['==', 'meta', 'vertex'], ['==', '$type', 'Point'], ['==', 'active', 'true']],                            paint: { 'circle-radius': 12, 'circle-color': '#fff' } },
    { id: 'gl-draw-polygon-and-line-vertex-active',          type: 'circle', filter: ['all', ['==', 'meta', 'vertex'], ['==', '$type', 'Point'], ['==', 'active', 'true']],                            paint: { 'circle-radius': 8,  'circle-color': '#ff6600' } },
    { id: 'gl-draw-polygon-midpoint', type: 'circle', filter: ['all', ['==', '$type', 'Point'], ['==', 'meta', 'midpoint']], paint: { 'circle-radius': 5, 'circle-color': '#ffffff' } },
];

export function generateRainbowPattern() {
    const size = 60;
    const canvas = document.createElement('canvas');
    canvas.width = size;
    canvas.height = size;
    const ctx = canvas.getContext('2d');
    const colors = ['#ff0000', '#ff8800', '#ffcc00', '#33cc55', '#3388ff', '#cc00cc'];
    const stripeH = size / colors.length;
    for (let i = 0; i < colors.length; i++) {
        ctx.fillStyle = colors[i];
        ctx.fillRect(0, i * stripeH, size, stripeH);
    }
    return ctx.getImageData(0, 0, size, size);
}

// onCampPolygonClick is passed in to avoid a circular dependency with edit.js
export function renderMap(onCampPolygonClick) {
    const { map } = appState;

    ['limit-zone-line', 'limit-zone-fill', 'camp-polygons-outline-warning', 'camp-polygons-outline-overlap', 'camp-polygons-outline', 'camp-polygons-fill-surprise', 'camp-polygons-fill', 'camp-polygons-labels'].forEach(id => {
        if (map.getLayer(id)) map.removeLayer(id);
    });
    ['limit-zone', 'camp-polygons'].forEach(id => {
        if (map.getSource(id)) map.removeSource(id);
    });

    if (appState.campMap.limitZoneGeoJson) {
        map.addSource('limit-zone', { type: 'geojson', data: JSON.parse(appState.campMap.limitZoneGeoJson) });
        map.addLayer({ id: 'limit-zone-fill', type: 'fill', source: 'limit-zone', paint: { 'fill-color': '#ffffff', 'fill-opacity': 0.08 } });
        map.addLayer({ id: 'limit-zone-line', type: 'line', source: 'limit-zone', paint: { 'line-color': '#ffffff', 'line-width': 2, 'line-dasharray': [4, 2] } });
    }

    const features = buildCampPolygonFeatures(appState.campMap.campPolygons);
    map.addSource('camp-polygons', { type: 'geojson', data: { type: 'FeatureCollection', features } });

    map.addLayer({
        id: 'camp-polygons-fill', type: 'fill', source: 'camp-polygons',
        filter: ['!=', ['get', 'soundZone'], 5],
        paint: {
            'fill-color': ['match', ['get', 'soundZone'],
                0, '#88aadd', 1, '#88bb88', 2, '#ddcc66', 3, '#ddaa66', 4, '#dd8888', '#aaaaaa'
            ],
            'fill-opacity': ['case', ['boolean', ['get', 'isOwn'], false], 0.4, 0.2],
        },
    });
    map.addLayer({
        id: 'camp-polygons-fill-surprise', type: 'fill', source: 'camp-polygons',
        filter: ['==', ['get', 'soundZone'], 5],
        paint: {
            'fill-pattern': 'rainbow-pattern',
            'fill-opacity': ['case', ['boolean', ['get', 'isOwn'], false], 0.55, 0.35],
        },
    });
    map.addLayer({
        id: 'camp-polygons-outline', type: 'line', source: 'camp-polygons',
        paint: {
            'line-color': ['match', ['get', 'soundZone'],
                0, '#2266cc', 1, '#229944', 2, '#cc9900', 3, '#cc6600', 4, '#cc1111', 5, '#cc00cc', '#666666'
            ],
            'line-width': ['case', ['boolean', ['get', 'isOwn'], false], 4, 1],
        },
    });
    map.addLayer({
        id: 'camp-polygons-outline-overlap', type: 'line', source: 'camp-polygons',
        filter: ['==', ['get', 'overlaps'], true],
        layout: { 'line-cap': 'round', 'line-join': 'round' },
        paint: { 'line-color': '#ff8800', 'line-width': 4, 'line-dasharray': [0, 2] },
    });
    map.addLayer({
        id: 'camp-polygons-outline-warning', type: 'line', source: 'camp-polygons',
        filter: ['==', ['get', 'outsideZone'], true],
        layout: { 'line-cap': 'round', 'line-join': 'round' },
        paint: { 'line-color': '#ff2222', 'line-width': 1.5, 'line-dasharray': [3, 5] },
    });
    map.addLayer({
        id: 'camp-polygons-labels', type: 'symbol', source: 'camp-polygons',
        layout: {
            'text-field': ['case',
                ['any', ['boolean', ['get', 'outsideZone'], false], ['boolean', ['get', 'overlaps'], false]],
                ['concat', '⚠️ ', ['get', 'campName']],
                ['get', 'campName'],
            ],
            'text-size': 14,
            'text-anchor': 'center',
            'text-allow-overlap': false,
        },
        paint: { 'text-color': '#000000', 'text-halo-color': '#ffffff', 'text-halo-width': 2 },
    });

    map.on('click', 'camp-polygons-fill', onCampPolygonClick);
    map.on('click', 'camp-polygons-fill-surprise', onCampPolygonClick);
    map.on('mouseenter', 'camp-polygons-fill',         () => { map.getCanvas().style.cursor = 'pointer'; });
    map.on('mouseenter', 'camp-polygons-fill-surprise', () => { map.getCanvas().style.cursor = 'pointer'; });
    map.on('mouseleave', 'camp-polygons-fill',         () => { map.getCanvas().style.cursor = ''; });
    map.on('mouseleave', 'camp-polygons-fill-surprise', () => { map.getCanvas().style.cursor = ''; });

    // Bring draw layers and warning overlays above our polygon layers
    map.getStyle().layers
        .filter(l => l.id.startsWith('gl-draw-'))
        .forEach(l => map.moveLayer(l.id));
    map.moveLayer('draw-warning-overlap');
    map.moveLayer('draw-warning-error');
    map.moveLayer('draw-edge-labels');
    map.moveLayer('draw-label');
}

export function setActivePolygonDim(campSeasonId) {
    const { map } = appState;
    const fillOpacity = campSeasonId
        ? ['case', ['==', ['get', 'campSeasonId'], campSeasonId], 0.1, ['boolean', ['get', 'isOwn'], false], 0.55, 0.35]
        : ['case', ['boolean', ['get', 'isOwn'], false], 0.55, 0.35];
    const surpriseOpacity = campSeasonId
        ? ['case', ['==', ['get', 'campSeasonId'], campSeasonId], 0.1, ['boolean', ['get', 'isOwn'], false], 0.75, 0.55]
        : ['case', ['boolean', ['get', 'isOwn'], false], 0.75, 0.55];
    if (map.getLayer('camp-polygons-fill'))         map.setPaintProperty('camp-polygons-fill',         'fill-opacity', fillOpacity);
    if (map.getLayer('camp-polygons-fill-surprise')) map.setPaintProperty('camp-polygons-fill-surprise', 'fill-opacity', surpriseOpacity);
}
