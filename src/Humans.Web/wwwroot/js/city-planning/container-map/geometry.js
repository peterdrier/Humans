// Builds and manipulates container polygons.
// A 20ft shipping container: 6m × 2.4m body + door triangle at door end.
// The door triangle has a half-width base (1.2m) centered on the door face.
// All functions depend on the global `turf` (loaded via CDN before this module).

const HALF_LEN_M      = 3;    // metres: half the container body length
const HALF_W_M        = 1.2;  // metres: half the container width
const TIP_DIST_M      = 4.2;  // metres: center → triangle tip (3 + 1.2)
const HALF_TRI_BASE_M = 0.6;  // metres: half the door triangle base (half of HALF_W_M)

// Returns index of the triangle tip vertex in the polygon coordinate ring (before closing).
export const TRIANGLE_TIP_INDEX = 4;

/**
 * Build a container polygon Feature (7-vertex shape: rectangle + half-width door triangle).
 * Unrotated orientation: door faces east (bearing 90°).
 * rotationDegrees: CCW angle passed to turf.transformRotate (0 = door east).
 *
 * Vertex order (CCW, GeoJSON outer ring):
 *   [0] back-NW  [1] back-SW  [2] front-SE  [3] tri-base-S  [4] tip  [5] tri-base-N  [6] front-NE  [7]==[0]
 */
export function buildContainerPolygon(centerLng, centerLat, rotationDegrees) {
    const center = [centerLng, centerLat];

    // Cardinal destinations from center (unrotated: east = door direction)
    const west = turf.destination(center, HALF_LEN_M, 270, { units: 'meters' }).geometry.coordinates;
    const east = turf.destination(center, HALF_LEN_M,  90, { units: 'meters' }).geometry.coordinates;
    const tip  = turf.destination(center, TIP_DIST_M,  90, { units: 'meters' }).geometry.coordinates;

    // Lat offsets for full width and half-width triangle base
    const northLat    = turf.destination(center, HALF_W_M,        0,   { units: 'meters' }).geometry.coordinates[1];
    const triNorthLat = turf.destination(center, HALF_TRI_BASE_M, 0,   { units: 'meters' }).geometry.coordinates[1];
    const dLat    = northLat    - centerLat;
    const dLatTri = triNorthLat - centerLat;

    const backNW     = [west[0], centerLat + dLat];
    const backSW     = [west[0], centerLat - dLat];
    const frontSE    = [east[0], centerLat - dLat];
    const triBaseS   = [east[0], centerLat - dLatTri];
    const triTip     = tip;
    const triBaseN   = [east[0], centerLat + dLatTri];
    const frontNE    = [east[0], centerLat + dLat];

    const ring = [backNW, backSW, frontSE, triBaseS, triTip, triBaseN, frontNE, backNW];

    const feature = turf.polygon([ring], {
        center_lng: centerLng,
        center_lat: centerLat,
        rotation_degrees: rotationDegrees,
    });

    return turf.transformRotate(feature, rotationDegrees, { pivot: center });
}

/**
 * Returns the [lng, lat] position to place the rotation handle (next to the door triangle)
 */
export function getRotationHandleCoordsForContainer(feature) {
    const coords = feature.geometry.coordinates[0];
    const s = coords[3]; // tri-base-S
    const n = coords[5]; // tri-base-N
    const baseCenter = [(s[0] + n[0]) / 2, (s[1] + n[1]) / 2];
    const tip = coords[4];
    return [
      tip[0] + (tip[0] - baseCenter[0]),
      tip[1] + (tip[1] - baseCenter[1]),
    ];
}

/**
 * Wraps a single Feature in a FeatureCollection for MapLibre source.setData().
 */
export function featureToCollection(feature) {
    return { type: 'FeatureCollection', features: [feature] };
}

/**
 * Converts the bearing of the mouse from the container center to a
 * turf.transformRotate-compatible rotation angle (CCW, 0 = door east).
 * bearing: CW from north (output of turf.bearing).
 */
export function rotationFromBearing(bearing) {
    return bearing - 90;
}
