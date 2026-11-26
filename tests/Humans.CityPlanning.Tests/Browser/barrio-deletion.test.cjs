const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const ring = count => Array.from({ length: count + 1 }, (_, i) => [i % count, 0]);

function deletion(type, coordinates, selectedCoordPaths) {
    let calls = 0;
    const state = { feature: { type, getCoordinates: () => coordinates }, selectedCoordPaths };
    const context = { MapboxDraw: { modes: { direct_select: { onTrash: received => {
        assert.equal(received, state); calls++;
    } } } } };
    const source = readFileSync(resolve(__dirname,
        '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/marquee-direct-select.js'), 'utf8')
        .replace('export const MarqueeDirectSelectMode', 'globalThis.MarqueeDirectSelectMode');
    vm.createContext(context); vm.runInContext(source, context);
    context.MarqueeDirectSelectMode.onTrash.call({}, state);
    return calls;
}

test('deleting one vertex cannot collapse a triangle', () => {
    assert.equal(deletion('Polygon', [ring(3)], ['0.0']), 0);
});

test('deleting several selected vertices cannot collapse an outer ring', () => {
    assert.equal(deletion('Polygon', [ring(4)], ['0.0', '0.1']), 0);
});

test('a hole cannot replace a collapsed outer boundary', () => {
    assert.equal(deletion('Polygon', [ring(4), ring(3)], ['0.0', '0.1']), 0);
});

test('every MultiPolygon outer ring retains its minimum vertices', () => {
    assert.equal(deletion('MultiPolygon', [[ring(4)], [ring(3)]], ['1.0.0']), 0);
});

test('a mixed selection is refused before any part can be changed', () => {
    assert.equal(deletion('MultiPolygon', [[ring(4)], [ring(4)]], ['0.0.0', '1.0.0', '1.0.1']), 0);
});

test('an untouched triangle does not block a valid edit in another part', () => {
    assert.equal(deletion('MultiPolygon', [[ring(4)], [ring(3)]], ['0.0.0']), 1);
});

test('valid deletions across several parts retain the native operation', () => {
    assert.equal(deletion('MultiPolygon', [[ring(4)], [ring(4)]], ['0.0.0', '1.0.0']), 1);
});

test('the drawing library retains hole-removal behavior', () => {
    assert.equal(deletion('Polygon', [ring(3), ring(3)], ['1.0', '1.1', '1.2']), 1);
});

test('a square can lose one vertex', () => {
    assert.equal(deletion('Polygon', [ring(4)], ['0.0']), 1);
});

test('line editing retains its native deletion behavior', () => {
    assert.equal(deletion('LineString', [[0, 0], [1, 1]], ['0']), 1);
});
