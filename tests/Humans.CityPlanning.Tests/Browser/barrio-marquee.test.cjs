const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const outer = [[-10, -10], [20, -10], [20, 20], [-10, 20], [-10, -10]];
const hole = [[2, 2], [8, 2], [8, 8], [2, 8], [2, 2]];

function select(type, coordinates, { previous = [], shift = false, rect = [0, 0, 10, 10] } = {}) {
    let selected = [];
    const feature = { type, getCoordinates: () => coordinates };
    // Polygon internals omit closing positions; MultiFeature deliberately has no coordinates field.
    if (type === 'Polygon') feature.coordinates = coordinates.map(ring => ring.slice(0, -1));
    const mode = {
        getFeature: () => feature,
        setSelectedCoordinates: points => { selected = points; },
        map: { project: point => {
            assert.equal(typeof point[0], 'number'); assert.equal(typeof point[1], 'number');
            return { x: point[0], y: point[1] };
        }, dragPan: { disable() {}, enable() {} }, getContainer: () => ({ appendChild() {} }) },
    };
    const context = {
        MapboxDraw: { modes: { direct_select: { onMouseDown() {}, onMouseUp() {} } } },
        document: { addEventListener() {}, createElement: () => ({ style: {}, remove() {} }) },
    };
    const source = readFileSync(resolve(__dirname,
        '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/marquee-direct-select.js'), 'utf8')
        .replace('export const MarqueeDirectSelectMode', 'globalThis.MarqueeDirectSelectMode');
    vm.createContext(context); vm.runInContext(source, context);
    const marquee = context.MarqueeDirectSelectMode;
    const state = { featureId: 'camp-A', selectedCoordPaths: previous };
    marquee.onMouseDown.call(mode, state, { point: { x: rect[0], y: rect[1] }, originalEvent: { button: 0, shiftKey: shift } });
    marquee.onDrag.call(mode, state, { point: { x: rect[2], y: rect[3] } });
    marquee.onMouseUp.call(mode, state, { point: { x: rect[2], y: rect[3] } });
    return JSON.parse(JSON.stringify(selected));
}
const paths = selection => selection.map(point => point.coord_path);

test('marquee selects hole vertices using their ring paths', () => {
    const selected = select('Polygon', [outer, hole]);
    assert.deepEqual(paths(selected), ['1.0', '1.1', '1.2', '1.3']);
    assert.ok(selected.every(point => point.feature_id === 'camp-A'));
});

test('marquee selects every MultiPolygon part with prefixed coordinate paths', () => {
    assert.deepEqual(paths(select('MultiPolygon', [[hole], [hole]])),
        ['0.0.0', '0.0.1', '0.0.2', '0.0.3', '1.0.0', '1.0.1', '1.0.2', '1.0.3']);
});

test('MultiPolygon hole vertices retain both polygon and ring indices', () => {
    assert.deepEqual(paths(select('MultiPolygon', [[outer], [outer, hole]])),
        ['1.1.0', '1.1.1', '1.1.2', '1.1.3']);
});

test('shift selection preserves earlier paths and deduplicates newly hit vertices', () => {
    assert.deepEqual(paths(select('Polygon', [outer, hole], { shift: true, previous: ['0.0', '1.0'] })),
        ['0.0', '1.0', '1.1', '1.2', '1.3']);
});

test('ordinary polygon selection excludes the closing duplicate', () => {
    assert.deepEqual(paths(select('Polygon', [hole])), ['0.0', '0.1', '0.2', '0.3']);
});

test('an empty marquee replaces an earlier selection', () => {
    assert.deepEqual(select('Polygon', [outer], { previous: ['0.0'] }), []);
});
