const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const root = resolve(__dirname, '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning');

function render(properties, { bare = false } = {}) {
    const layers = [], sources = {};
    const feature = { type: 'Feature', geometry: { type: 'Polygon', coordinates: [] }, properties };
    const state = { campMap: { campPolygons: [], limitZoneGeoJson: JSON.stringify(bare ? feature.geometry : feature) } };
    state.map = { getStyle: () => ({ layers: [] }), getLayer: () => null, getSource: () => null,
        addSource: (id, source) => { sources[id] = source.data; }, addLayer: layer => layers.push(layer), on() {}, moveLayer() {} };
    const context = { appState: state, buildCampPolygonFeatures: () => [], addOfficialZonesLayers() {} };
    vm.createContext(context);
    for (const file of ['shared/sound-zone-colors.js', 'barrio-map/layers.js']) {
        vm.runInContext(readFileSync(resolve(root, file), 'utf8')
            .replace(/^import[^\n]*;\s*$/gm, '').replaceAll('export ', ''), context);
    }
    context.renderMap(() => {});
    const data = sources['limit-zone'];
    const drawnFeature = data.type === 'FeatureCollection' ? data.features[0] : data;
    function evaluate(expr) {
        if (!Array.isArray(expr)) return expr;
        const [operator, ...args] = expr;
        if (operator === 'get') return drawnFeature.properties?.[args[0]] ?? null;
        if (operator === 'has') return Object.hasOwn(drawnFeature.properties ?? {}, args[0]);
        if (operator === 'coalesce') return args.map(evaluate).find(value => value !== null) ?? null;
        if (operator === '==') return evaluate(args[0]) === evaluate(args[1]);
        if (operator === '!') return !evaluate(args[0]);
        if (operator === 'all') return args.every(evaluate);
        throw new Error(`Unexpected filter operator ${operator}`);
    }
    return { drawnFeature, outlines: layers.filter(layer => layer.id.startsWith('limit-zone-line') && evaluate(layer.filter)) };
}

for (const value of [42, true, [], {}]) {
    test(`non-string ${JSON.stringify(value)} sound metadata renders an unassigned outline`, () => {
        const result = render({ SoundZone: value });
        assert.equal(result.outlines.length, 1);
        assert.equal(result.outlines[0].id, 'limit-zone-line-fallback');
        assert.deepEqual(JSON.parse(JSON.stringify(result.drawnFeature.properties.SoundZone)), value);
    });
}

test('an empty current property still renders the valid legacy zone', () => {
    const result = render({ SoundZone: '', sound_zone: 'blue' });
    assert.equal(result.outlines.length, 1);
    assert.equal(result.outlines[0].paint['line-color'], '#2266cc');
});

test('an unusable current property does not hide a valid legacy zone', () => {
    const result = render({ SoundZone: 42, sound_zone: 'red' });
    assert.equal(result.outlines.length, 1);
    assert.equal(result.outlines[0].paint['line-color'], '#cc1111');
});

test('valid current metadata retains precedence and colour', () => {
    const result = render({ SoundZone: 'blue', sound_zone: 'red' });
    assert.equal(result.outlines.length, 1);
    assert.equal(result.outlines[0].paint['line-color'], '#2266cc');
});

test('missing sound metadata retains the unassigned outline', () => {
    const result = render(null);
    assert.equal(result.outlines.length, 1);
    assert.equal(result.outlines[0].id, 'limit-zone-line-fallback');
});

test('a bare overlay geometry keeps its boundary and coordinates', () => {
    const result = render(null, { bare: true });
    const geometry = result.drawnFeature.type === 'Feature' ? result.drawnFeature.geometry : result.drawnFeature;
    assert.equal(geometry.type, 'Polygon');
    assert.deepEqual(JSON.parse(JSON.stringify(geometry.coordinates)), []);
    assert.equal(result.outlines.length, 1);
    assert.equal(result.outlines[0].id, 'limit-zone-line-fallback');
});
