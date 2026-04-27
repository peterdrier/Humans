// Builds and manipulates container pentagon polygons.
// A 20ft shipping container: 6m × 2.4m body + 1.2m triangle at door end.
// All functions depend on the global `turf` (loaded via CDN before this module).

const HALF_LEN_M  = 3;    // metres: half the container body length
const HALF_W_M    = 1.2;  // metres: half the container width
const TIP_DIST_M  = 4.2;  // metres: center → triangle tip (3 + 1.2)

// Returns index of the triangle tip vertex in the polygon coordinate ring (before closing).
export const TRIANGLE_TIP_INDEX = 3;

/**
 * Build a container pentagon Feature.
 * Unrotated orientation: door faces east (bearing 90°).
 * rotationDegrees: CCW angle passed to turf.transformRotate (0 = door east).
 *
 * Vertex order (CCW, GeoJSON outer ring):
 *   [0] back-NW  [1] back-SW  [2] front-SE  [3] tip  [4] front-NE  [5]==[0]
 */
export function buildContainerPolygon(centerLng, centerLat, rotationDegrees) {
    const center = [centerLng, centerLat];

    // Cardinal destinations from center (unrotated: east = door direction)
    const west = turf.destination(center, HALF_LEN_M, 270, { units: 'meters' }).geometry.coordinates;
    const east = turf.destination(center, HALF_LEN_M,  90, { units: 'meters' }).geometry.coordinates;
    const tip  = turf.destination(center, TIP_DIST_M,  90, { units: 'meters' }).geometry.coordinates;

    // Lat offset for width (same at any longitude direction)
    const northLat = turf.destination(center, HALF_W_M, 0, { units: 'meters' }).geometry.coordinates[1];
    const southLat = turf.destination(center, HALF_W_M, 180, { units: 'meters' }).geometry.coordinates[1];
    const dLat = northLat - centerLat; // positive delta

    const backNW  = [west[0], centerLat + dLat];
    const backSW  = [west[0], centerLat - dLat];
    const frontSE = [east[0], centerLat - dLat];
    const triTip  = tip;
    const frontNE = [east[0], centerLat + dLat];

    const ring = [backNW, backSW, frontSE, triTip, frontNE, backNW];

    const feature = turf.polygon([ring], {
        center_lng: centerLng,
        center_lat: centerLat,
        rotation_degrees: rotationDegrees,
    });

    return turf.transformRotate(feature, rotationDegrees, { pivot: center });
}

/**
 * Returns the [lng, lat] of the triangle tip vertex from a built polygon.
 * Used to position the rotation handle DOM element.
 */
export function getTriangleTipCoords(feature) {
    return feature.geometry.coordinates[0][TRIANGLE_TIP_INDEX];
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
    return 90 - bearing;
}
