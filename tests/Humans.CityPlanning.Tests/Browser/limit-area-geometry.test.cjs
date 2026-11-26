const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const root = resolve(__dirname, '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning');
const polygon = { type: 'Polygon', coordinates: [[[0, 0], [4, 0], [4, 4], [0, 0]]] };
const wrap = (geometry, properties = {}) => ({ type: 'Feature', properties, geometry });
const collection = features => ({ type: 'FeatureCollection', features });

function calculations(overlay) {
    let unions = 0;
    function check(features) {
        assert.ok(features.every(f => f.type === 'Feature' && ['Polygon', 'MultiPolygon'].includes(f.geometry?.type)));
        assert.ok(features.every(f => f.geometry.coordinates.length > 0));
    }
    const context = {
        appState: { campMap: { limitZoneGeoJson: JSON.stringify(overlay) } }, CONFIG: {}, console,
        turf: {
            featureCollection: collection,
            union: data => { check(data.features); unions++; return data.features[0]; },
            difference: data => { check(data.features); return wrap(polygon); },
            intersect: data => { check(data.features); return wrap(polygon); }, area: () => 10,
        },
    };
    vm.createContext(context);
    for (const file of ['shared/sound-zone-colors.js', 'barrio-map/geometry.js']) {
        vm.runInContext(readFileSync(resolve(root, file), 'utf8')
            .replace(/^import[^\n]*;\s*$/gm, '').replaceAll('export ', ''), context);
    }
    return {
        parse: () => context.parseLimitZoneGeom(JSON.stringify(overlay)), unions: () => unions,
        outside(boundary) { context.appState.limitZoneGeom = boundary; return context.isOutsideZone(wrap(polygon)); },
        mismatch: () => context.getSoundZoneOutOfRange(wrap(polygon), 4),
    };
}

test('a mixed overlay collection uses only area features for its boundary', () => {
    const ui = calculations(collection([wrap(polygon), wrap({ type: 'Point', coordinates: [0, 0] }), wrap(null)]));
    const boundary = ui.parse();
    assert.equal(boundary.geometry.type, 'Polygon');
    assert.equal(ui.unions(), 0);
    assert.equal(ui.outside(boundary), true);
});

test('a bare area becomes a Feature before out-of-bounds calculations', () => {
    const ui = calculations(polygon); const boundary = ui.parse();
    assert.equal(boundary.type, 'Feature');
    assert.equal(ui.outside(boundary), true);
});

test('null and non-area-only overlays have no containment boundary', () => {
    for (const overlay of [wrap(null), wrap({ type: 'Point', coordinates: [] }),
        collection([wrap(null), wrap({ type: 'LineString', coordinates: [[0, 0], [1, 1]] })])]) {
        assert.equal(calculations(overlay).parse(), null);
    }
});

test('nested GeometryCollections retain area parts and sound-zone metadata', () => {
    const geometry = { type: 'GeometryCollection', geometries: [
        { type: 'Point', coordinates: [0, 0] },
        { type: 'GeometryCollection', geometries: [polygon] },
    ] };
    const ui = calculations(wrap(geometry, { SoundZone: 'blue', custom: 'retained' }));
    const boundary = ui.parse();
    assert.equal(boundary.geometry.type, 'Polygon');
    assert.equal(boundary.properties.custom, 'retained');
    assert.equal(ui.mismatch(), true);
});

test('empty area parts are ignored without dropping usable MultiPolygon parts', () => {
    const ui = calculations(wrap({ type: 'MultiPolygon', coordinates: [[], polygon.coordinates] }));
    assert.deepEqual(JSON.parse(JSON.stringify(ui.parse().geometry.coordinates)), polygon.coordinates);
    assert.equal(calculations(wrap({ type: 'Polygon', coordinates: [] })).parse(), null);
});

test('several area regions are still unioned', () => {
    const ui = calculations(collection([wrap(polygon), wrap(polygon)]));
    assert.equal(ui.parse().type, 'Feature');
    assert.equal(ui.unions(), 1);
});

test('multipart sound regions remain grouped for intersection-area comparison', () => {
    const ui = calculations(wrap({ type: 'MultiPolygon', coordinates: [polygon.coordinates, polygon.coordinates] }, { SoundZone: 'blue' }));
    assert.equal(ui.parse().geometry.type, 'MultiPolygon');
    assert.equal(ui.mismatch(), true);
});
