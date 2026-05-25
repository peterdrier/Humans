// Custom drag-to-move and drag-handle-to-rotate interaction for container placement.

import { buildContainerPolygon, getRotationHandleCoordsForContainer, rotationFromBearing } from './geometry.js';
import { updateActiveSource } from './layers.js';

let _map         = null;
let _onSave      = null; // async callback(container, feature) — called on mouseup
let _onSelect    = null; // callback(featureId) — called when user clicks a placed editable container

// Active container state
let _activeContainer = null; // { id, campId, canEdit }
let _currentCenter   = null; // { lng, lat }
let _currentRotation = 0;    // CCW degrees (turf convention)
let _activeFeature   = null;

// Drag-to-move state
let _isDragging      = false;
let _dragStartLngLat = null;
let _dragStartCenter = null;

// Drag-to-rotate state
let _isRotating = false;

const rotationHandle = document.getElementById('rotation-handle');
const mapCanvas      = () => _map.getCanvas();

export function initInteraction(map, onSave, onSelect) {
    _map      = map;
    _onSave   = onSave;
    _onSelect = onSelect;

    // Drag-to-move: mousedown on the active or editable fill layer
    map.on('mousedown', 'containers-active-fill',   onContainerMouseDown);
    map.on('mousedown', 'containers-editable-fill', onContainerMouseDown);

    // Click on placed editable container to select it
    map.on('click', 'containers-editable-fill', onContainerClick);

    // Map-wide mousemove and mouseup for drag
    map.on('mousemove', onMapMouseMove);
    map.on('mouseup', onMapMouseUp);

    // Reposition handle on map move/zoom
    map.on('move', repositionHandle);
    map.on('zoom', repositionHandle);

    // Rotation handle events (document-level to capture outside map bounds)
    rotationHandle.addEventListener('mousedown', onHandleMouseDown);
    document.addEventListener('mousemove', onDocumentMouseMove);
    document.addEventListener('mouseup', onDocumentMouseUp);
}

/**
 * Activate a container: place its polygon at (centerLng, centerLat) with rotation 0.
 * Used when user clicks an unplaced card in the sidebar.
 */
export function activateContainer(container, centerLng, centerLat) {
    _activeContainer = container;
    _currentCenter   = { lng: centerLng, lat: centerLat };
    _currentRotation = 0;
    _activeFeature   = buildContainerPolygon(centerLng, centerLat, 0);
    _activeFeature.properties.name = container.name;

    updateActiveSource(_map, _activeFeature);
    repositionHandle();
    rotationHandle.style.display = 'flex';

    _map.easeTo({ center: [centerLng, centerLat], zoom: 19, duration: 300 });
}

/**
 * Select an already-placed container for repositioning.
 */
export function selectPlacedContainer(container) {
    const f = JSON.parse(container.locationGeoJson);
    _activeContainer = container;
    _currentCenter   = { lng: f.properties.center_lng, lat: f.properties.center_lat };
    _currentRotation = f.properties.rotation_degrees;
    _activeFeature   = f;
    _activeFeature.properties.name = container.name;

    updateActiveSource(_map, _activeFeature);
    repositionHandle();
    rotationHandle.style.display = 'flex';

    _map.easeTo({ center: [_currentCenter.lng, _currentCenter.lat], zoom: Math.max				(_map.getZoom(), 19), duration: 300 });
}

/**
 * Deactivate the current container (e.g. another card was clicked).
 */
export function deactivate() {
    _activeContainer = null;
    _activeFeature   = null;
    updateActiveSource(_map, null);
    rotationHandle.style.display = 'none';
}

// ── Move drag ──────────────────────────────────────────────────────────────

function onContainerMouseDown(e) {
    // Only move the active container
    if (!_activeContainer) return;
    const featureId = e.features?.[0]?.properties?.id;
    if (featureId && featureId !== _activeContainer.id) return;

    e.preventDefault(); // prevent map pan
    _isDragging      = true;
    _dragStartLngLat = e.lngLat;
    _dragStartCenter = { ..._currentCenter };
    mapCanvas().style.cursor = 'grabbing';
}

function onContainerClick(e) {
    if (_isDragging) return;
    const featureId = e.features?.[0]?.properties?.id;
    if (!featureId) return;
    _onSelect?.(featureId);
}

// rAF coalescer for drag/rotate mousemove. Mousemove fires at 60-120Hz; the
// active-source setData + handle reposition only need to run once per frame.
let _pendingDrag   = null;   // { lng, lat } from last drag mousemove
let _pendingRotate = null;   // bearing degrees from last rotate mousemove
let _rafScheduled  = false;
function scheduleRender() {
    if (_rafScheduled) return;
    _rafScheduled = true;
    requestAnimationFrame(() => {
        _rafScheduled = false;
        if (_pendingDrag && _activeContainer) {
            _currentCenter = _pendingDrag;
            _pendingDrag = null;
        }
        if (_pendingRotate !== null && _activeContainer) {
            _currentRotation = rotationFromBearing(_pendingRotate);
            _pendingRotate = null;
        }
        if (!_activeContainer) return;
        _activeFeature = buildContainerPolygon(_currentCenter.lng, _currentCenter.lat, _currentRotation);
        _activeFeature.properties.name = _activeContainer.name;
        updateActiveSource(_map, _activeFeature);
        repositionHandle();
    });
}

function onMapMouseMove(e) {
    if (!_isDragging) return;
    const dLng = e.lngLat.lng - _dragStartLngLat.lng;
    const dLat = e.lngLat.lat - _dragStartLngLat.lat;
    _pendingDrag = {
        lng: _dragStartCenter.lng + dLng,
        lat: _dragStartCenter.lat + dLat,
    };
    scheduleRender();
}

async function onMapMouseUp() {
    if (!_isDragging) return;
    _isDragging = false;
    mapCanvas().style.cursor = '';
    if (_activeContainer && _activeFeature) {
        await _onSave?.(_activeContainer, _activeFeature);
    }
}

// ── Rotate drag ────────────────────────────────────────────────────────────

function onHandleMouseDown(e) {
    if (!_activeContainer) return;
    _isRotating = true;
    e.preventDefault();
    e.stopPropagation();
}

function onDocumentMouseMove(e) {
    if (!_isRotating || !_activeContainer) return;

    const rect  = mapCanvas().getBoundingClientRect();
    const point = { x: e.clientX - rect.left, y: e.clientY - rect.top };
    const lngLat = _map.unproject(point);

    _pendingRotate = turf.bearing(
        turf.point([_currentCenter.lng, _currentCenter.lat]),
        turf.point([lngLat.lng, lngLat.lat]),
    );
    scheduleRender();
}

async function onDocumentMouseUp() {
    if (!_isRotating) return;
    _isRotating = false;
    if (_activeContainer && _activeFeature) {
        await _onSave?.(_activeContainer, _activeFeature);
    }
}

// ── Handle positioning ─────────────────────────────────────────────────────

function repositionHandle() {
    if (!_activeFeature || !_map) return;
    const backCenter = getRotationHandleCoordsForContainer(_activeFeature);
    const pixel      = _map.project(backCenter);
    rotationHandle.style.left = `${pixel.x}px`;
    rotationHandle.style.top  = `${pixel.y}px`;
}
