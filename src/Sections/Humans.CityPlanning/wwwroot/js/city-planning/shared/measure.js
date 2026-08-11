// Measuring tool: two-point distance with rubber-band preview, multi-measurement support.

const EMPTY_FC = { type: 'FeatureCollection', features: [] };

let _map          = null;
let _active       = false;
let _measurements = []; // Array<{ id: string, a: [lng,lat], b: [lng,lat] }>
let _pending      = null; // { a: [lng,lat] } | null — in-flight first point
let _onMouseMove  = null;

function newId() {
    return crypto.randomUUID();
}

function formatDistance(meters) {
    if (meters >= 1000) return (meters / 1000).toFixed(2) + ' km';
    return Math.round(meters) + ' m';
}

function midpoint(a, b) {
    return [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2];
}

function distanceLabel(a, b) {
    const meters = turf.distance(turf.point(a), turf.point(b), { units: 'meters' });
    return formatDistance(meters);
}

function updateClearBtn() {
    const btn = document.getElementById('clear-measurements-btn');
    if (!btn) return;
    btn.classList.toggle('d-none', _measurements.length === 0);
}

function renderMeasurements() {
    const pointFeatures = [];
    for (const m of _measurements) {
        pointFeatures.push({
            type: 'Feature',
            geometry: { type: 'Point', coordinates: m.a },
            properties: { measurementId: m.id },
        });
        pointFeatures.push({
            type: 'Feature',
            geometry: { type: 'Point', coordinates: m.b },
            properties: { measurementId: m.id },
        });
    }
    if (_pending) {
        pointFeatures.push({
            type: 'Feature',
            geometry: { type: 'Point', coordinates: _pending.a },
            properties: {},
        });
    }
    _map.getSource('measure-points').setData({ type: 'FeatureCollection', features: pointFeatures });

    _map.getSource('measure-line').setData({
        type: 'FeatureCollection',
        features: _measurements.map(m => ({
            type: 'Feature',
            geometry: { type: 'LineString', coordinates: [m.a, m.b] },
            properties: { measurementId: m.id },
        })),
    });

    _map.getSource('measure-label').setData({
        type: 'FeatureCollection',
        features: _measurements.map(m => ({
            type: 'Feature',
            geometry: { type: 'Point', coordinates: midpoint(m.a, m.b) },
            properties: { measurementId: m.id, label: distanceLabel(m.a, m.b) },
        })),
    });

    _map.getSource('measure-preview-line').setData(EMPTY_FC);
}

function attachMouseMove() {
    detachMouseMove();
    _onMouseMove = e => {
        if (!_pending) return;
        const cursor = [e.lngLat.lng, e.lngLat.lat];
        _map.getSource('measure-preview-line').setData({
            type: 'FeatureCollection',
            features: [{ type: 'Feature', geometry: { type: 'LineString', coordinates: [_pending.a, cursor] }, properties: {} }],
        });
        // Add live preview label alongside committed labels
        _map.getSource('measure-label').setData({
            type: 'FeatureCollection',
            features: [
                ..._measurements.map(m => ({
                    type: 'Feature',
                    geometry: { type: 'Point', coordinates: midpoint(m.a, m.b) },
                    properties: { measurementId: m.id, label: distanceLabel(m.a, m.b) },
                })),
                {
                    type: 'Feature',
                    geometry: { type: 'Point', coordinates: midpoint(_pending.a, cursor) },
                    properties: { label: distanceLabel(_pending.a, cursor) },
                },
            ],
        });
    };
    _map.on('mousemove', _onMouseMove);
}

function detachMouseMove() {
    if (_onMouseMove) {
        _map.off('mousemove', _onMouseMove);
        _onMouseMove = null;
    }
}

function onMapClick(e) {
    const coord = [e.lngLat.lng, e.lngLat.lat];

    // 1. First click: start pending measurement
    if (!_pending) {
        _pending = { a: coord };
        attachMouseMove();
        renderMeasurements();
        return;
    }

    // 2. Second click: complete measurement and exit measure mode
    _measurements.push({ id: newId(), a: _pending.a, b: coord });
    exitMeasureMode();
    updateClearBtn();
}

function onMapContextMenu(e) {
    // Right-click on an existing measurement deletes it.
    const hits = _map.queryRenderedFeatures(e.point, { layers: ['measure-points', 'measure-label'] });
    const hitWithId = hits.find(f => f.properties.measurementId);
    if (!hitWithId) return;
    e.preventDefault();
    _measurements = _measurements.filter(m => m.id !== hitWithId.properties.measurementId);
    renderMeasurements();
    updateClearBtn();
}

export function enterMeasureMode() {
    if (_active) return;
    _active = true;
    _map.getCanvas().style.cursor = 'crosshair';
    _map.on('click', onMapClick);
    _map.on('contextmenu', onMapContextMenu);

    const btn = document.getElementById('measure-btn');
    btn.classList.remove('btn-outline-secondary');
    btn.classList.add('btn-warning');
    btn.setAttribute('aria-pressed', 'true');
}

export function exitMeasureMode() {
    if (!_active) return;
    detachMouseMove();
    _map.off('click', onMapClick);
    _map.off('contextmenu', onMapContextMenu);
    _pending = null;
    _active = false;
    _map.getCanvas().style.cursor = '';
    renderMeasurements();

    const btn = document.getElementById('measure-btn');
    btn.classList.remove('btn-warning');
    btn.classList.add('btn-outline-secondary');
    btn.setAttribute('aria-pressed', 'false');
}

export function isMeasuring() { return _active; }

export function clearAllMeasurements() {
    _measurements = [];
    renderMeasurements();
    updateClearBtn();
}

/**
 * Wire the standard `#measure-btn` (toggle) and `#clear-measurements-btn` (clear-all) pair.
 * @param {{ beforeEnter?: () => void }} [opts] — `beforeEnter` runs once, just before
 *   transitioning into measure mode (e.g. to exit a conflicting edit/selection mode).
 */
export function wireMeasureButtons({ beforeEnter } = {}) {
    document.getElementById('measure-btn')?.addEventListener('click', () => {
        if (isMeasuring()) { exitMeasureMode(); return; }
        beforeEnter?.();
        enterMeasureMode();
    });
    document.getElementById('clear-measurements-btn')?.addEventListener('click', clearAllMeasurements);
}

export function initMeasure(map) {
    _map = map;

    // Resolve design-system tokens at init so map paint stays in sync with the palette.
    const styles = getComputedStyle(document.documentElement);
    const inkColor   = styles.getPropertyValue('--h-aged-ink').trim()   || '#3d2b1f';
    const haloColor  = styles.getPropertyValue('--h-warm-white').trim() || '#fefaf3';

    map.addSource('measure-points',       { type: 'geojson', data: EMPTY_FC });
    map.addSource('measure-line',         { type: 'geojson', data: EMPTY_FC });
    map.addSource('measure-preview-line', { type: 'geojson', data: EMPTY_FC });
    map.addSource('measure-label',        { type: 'geojson', data: EMPTY_FC });

    map.addLayer({
        id: 'measure-line', type: 'line', source: 'measure-line',
        paint: { 'line-color': inkColor, 'line-width': 2, 'line-dasharray': [2, 2] },
    });
    map.addLayer({
        id: 'measure-preview-line', type: 'line', source: 'measure-preview-line',
        paint: { 'line-color': inkColor, 'line-width': 2, 'line-dasharray': [2, 2], 'line-opacity': 0.5 },
    });
    map.addLayer({
        id: 'measure-label', type: 'symbol', source: 'measure-label',
        layout: { 'text-field': ['get', 'label'], 'text-size': 13, 'text-anchor': 'center', 'text-allow-overlap': true },
        paint: { 'text-color': inkColor, 'text-halo-color': haloColor, 'text-halo-width': 2 },
    });
    map.addLayer({
        id: 'measure-points', type: 'symbol', source: 'measure-points',
        layout: {
            'text-field': '+',
            'text-size': 22,
            'text-anchor': 'center',
            'text-allow-overlap': true,
            'text-ignore-placement': true,
        },
        paint: { 'text-color': inkColor, 'text-halo-color': haloColor, 'text-halo-width': 2 },
    });
}
