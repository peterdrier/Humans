// Measuring tool: two-point distance with rubber-band preview.
import { appState } from './state.js';
import { exitEditMode } from './edit.js';

const EMPTY_FC = { type: 'FeatureCollection', features: [] };

let firstPoint = null;   // [lng, lat] or null
let secondPoint = null;  // [lng, lat] or null

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

function setPoints(pts) {
    appState.map.getSource('measure-points').setData({
        type: 'FeatureCollection',
        features: pts.map(p => ({ type: 'Feature', geometry: { type: 'Point', coordinates: p }, properties: {} })),
    });
}

function setLine(a, b) {
    appState.map.getSource('measure-line').setData({
        type: 'FeatureCollection',
        features: [{ type: 'Feature', geometry: { type: 'LineString', coordinates: [a, b] }, properties: {} }],
    });
}

function setPreviewLine(a, b) {
    appState.map.getSource('measure-preview-line').setData({
        type: 'FeatureCollection',
        features: [{ type: 'Feature', geometry: { type: 'LineString', coordinates: [a, b] }, properties: {} }],
    });
}

function setLabel(pos, text) {
    appState.map.getSource('measure-label').setData({
        type: 'FeatureCollection',
        features: [{ type: 'Feature', geometry: { type: 'Point', coordinates: pos }, properties: { label: text } }],
    });
}

function clearAll() {
    appState.map.getSource('measure-points').setData(EMPTY_FC);
    appState.map.getSource('measure-line').setData(EMPTY_FC);
    appState.map.getSource('measure-preview-line').setData(EMPTY_FC);
    appState.map.getSource('measure-label').setData(EMPTY_FC);
}

// mousemove handler reference so we can remove it on exit
let onMouseMove = null;

function attachMouseMove() {
    detachMouseMove();
    onMouseMove = e => {
        if (!firstPoint) return;
        const cursor = [e.lngLat.lng, e.lngLat.lat];
        setPreviewLine(firstPoint, cursor);
        const mid = midpoint(firstPoint, cursor);
        setLabel(mid, distanceLabel(firstPoint, cursor));
    };
    appState.map.on('mousemove', onMouseMove);
}

function detachMouseMove() {
    if (onMouseMove) {
        appState.map.off('mousemove', onMouseMove);
        onMouseMove = null;
    }
}

function onMapClick(e) {
    const coord = [e.lngLat.lng, e.lngLat.lat];

    if (!firstPoint) {
        // Place first point
        firstPoint = coord;
        secondPoint = null;
        setPoints([firstPoint]);
        appState.map.getSource('measure-line').setData(EMPTY_FC);
        appState.map.getSource('measure-label').setData(EMPTY_FC);
        attachMouseMove();
        return;
    }

    if (!secondPoint) {
        // Place second point — lock in measurement
        secondPoint = coord;
        setPoints([firstPoint, secondPoint]);
        setLine(firstPoint, secondPoint);
        const mid = midpoint(firstPoint, secondPoint);
        setLabel(mid, distanceLabel(firstPoint, secondPoint));
        appState.map.getSource('measure-preview-line').setData(EMPTY_FC);
        detachMouseMove();
        return;
    }

    // Third click: start a new measurement from this point
    firstPoint = coord;
    secondPoint = null;
    setPoints([firstPoint]);
    appState.map.getSource('measure-line').setData(EMPTY_FC);
    appState.map.getSource('measure-label').setData(EMPTY_FC);
    attachMouseMove();
}

export function enterMeasureMode() {
    if (appState.measuringActive) return;
    if (appState.activeCampSeasonId) exitEditMode();

    appState.measuringActive = true;
    firstPoint = null;
    secondPoint = null;
    clearAll();
    appState.map.getCanvas().style.cursor = 'crosshair';
    appState.map.on('click', onMapClick);

    const btn = document.getElementById('measure-btn');
    btn.classList.remove('btn-outline-secondary');
    btn.classList.add('btn-warning');
    btn.setAttribute('aria-pressed', 'true');
}

export function exitMeasureMode() {
    if (!appState.measuringActive) return;

    detachMouseMove();
    appState.map.off('click', onMapClick);
    clearAll();

    firstPoint = null;
    secondPoint = null;
    appState.measuringActive = false;
    appState.map.getCanvas().style.cursor = '';

    const btn = document.getElementById('measure-btn');
    btn.classList.remove('btn-warning');
    btn.classList.add('btn-outline-secondary');
    btn.setAttribute('aria-pressed', 'false');
}

export function initMeasure(map) {
    if (typeof turf === 'undefined') throw new Error('measure.js requires turf.js to be loaded globally');
    map.addSource('measure-points',       { type: 'geojson', data: EMPTY_FC });
    map.addSource('measure-line',         { type: 'geojson', data: EMPTY_FC });
    map.addSource('measure-preview-line', { type: 'geojson', data: EMPTY_FC });
    map.addSource('measure-label',        { type: 'geojson', data: EMPTY_FC });

    map.addLayer({
        id: 'measure-line', type: 'line', source: 'measure-line',
        paint: { 'line-color': '#ff6600', 'line-width': 2, 'line-dasharray': [2, 2] },
    });
    map.addLayer({
        id: 'measure-preview-line', type: 'line', source: 'measure-preview-line',
        paint: { 'line-color': '#ff6600', 'line-width': 2, 'line-dasharray': [2, 2], 'line-opacity': 0.5 },
    });
    map.addLayer({
        id: 'measure-label', type: 'symbol', source: 'measure-label',
        layout: { 'text-field': ['get', 'label'], 'text-size': 13, 'text-anchor': 'center', 'text-allow-overlap': true },
        paint: { 'text-color': '#000000', 'text-halo-color': '#ffffff', 'text-halo-width': 2 },
    });
    map.addLayer({
        id: 'measure-points-stroke', type: 'circle', source: 'measure-points',
        paint: { 'circle-radius': 10, 'circle-color': '#fff' },
    });
    map.addLayer({
        id: 'measure-points', type: 'circle', source: 'measure-points',
        paint: { 'circle-radius': 7, 'circle-color': '#ff6600' },
    });
}
