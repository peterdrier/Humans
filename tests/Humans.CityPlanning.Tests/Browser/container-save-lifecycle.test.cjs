const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const { installHumans } = require('../../browser-support/humans-js.cjs');
const root = resolve(__dirname, '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map');
const flush = () => new Promise(setImmediate);
const feature = () => ({ type: 'Feature', geometry: { type: 'Polygon', coordinates: [] },
    properties: { center_lng: 0, center_lat: 0, rotation_degrees: 0 } });

async function editor({ placedA = false, placedB = true } = {}) {
    const rows = ['A', 'B'].map(id => ({ id, name: id, canEdit: true,
        locationGeoJson: (id === 'A' ? placedA : placedB) ? JSON.stringify(feature()) : null }));
    const requests = [], scrolls = [], alerts = [], sources = {};
    let callbacks, save, measure;
    function section() {
        const list = { cards: [], querySelector: selector => selector === '.sidebar-section-label' ? null
            : list.cards.find(card => selector === `[data-id="${card.dataset.id}"]`) ?? null,
        appendChild: card => list.cards.push(card) };
        Object.defineProperty(list, 'innerHTML', { set: () => { list.cards = []; } });
        return list;
    }
    const sections = { 'sidebar-unplaced': section(), 'sidebar-placed': section() };
    const handle = { style: {}, addEventListener() {} };
    const toast = { style: {}, textContent: '' };
    const map = {
        on: (event, handler) => { if (event === 'load') queueMicrotask(handler); },
        getSource: name => ({ setData: data => { sources[name] = data; } }),
        getCanvas: () => ({ style: {} }), project: () => ({ x: 0, y: 0 }),
        easeTo() {}, getZoom: () => 19,
    };
    const context = {
        CONFIG: { YEAR: 2026, IS_MAP_ADMIN: true, MAP_BOUNDS: [[0, 0], [2, 2]], ESRI_TILES: [],
            I18N: { CONFIRM_CLEAR: 'Clear {0}', CLEAR_PLACEMENT: 'Clear' } },
        document: {
            getElementById: id => sections[id] ?? (id === 'rotation-handle' ? handle : toast),
            createElement: tagName => ({ tagName, dataset: {}, querySelector: () => ({ addEventListener() {} }),
                addEventListener() {}, scrollIntoView() { scrolls.push(this.dataset.id); } }),
            addEventListener() {},
        },
        maplibregl: { Map: function () { return map; } },
        fetch: async () => ({ json: async () => ({ campPolygons: [] }) }),
        loadContainers: async () => rows,
        savePlacement: (id, year, geoJson) => new Promise((complete, fail) => requests.push({ id, geoJson, complete, fail })),
        clearPlacement: id => new Promise((complete, fail) => requests.push({ id, complete, fail })),
        addBackgroundLayers() {}, addContainerLayers() {}, initMeasure() {}, exitMeasureMode() {},
        wireMeasureButtons: options => { measure = options.beforeEnter; },
        initPlacementNotes() {}, openPlacementNotes() {},
        buildContainerPolygon: feature, getRotationHandleCoordsForContainer: () => [0, 0], rotationFromBearing: value => value,
        confirm: () => true, setTimeout() {}, requestAnimationFrame() {},
        console: { error: (...args) => alerts.push(args) },
    };
    installHumans(context); vm.createContext(context);
    function load(file, suffix = '') {
        const original = readFileSync(resolve(root, file), 'utf8');
        const exports = [...original.matchAll(/export (?:async )?function (\w+)/g)].map(match => match[1]);
        const code = original.replace(/^import[^\n]*;\s*$/gm, '').replaceAll('export ', '');
        vm.runInContext(`(function () { ${code}\nObject.assign(globalThis, { ${exports.join(',')} }); ${suffix} })();`, context);
    }
    load('sidebar.js'); load('interaction.js');
    const originalSidebar = context.initSidebar, originalInteraction = context.initInteraction;
    context.initSidebar = (...args) => { callbacks = args; originalSidebar(...args); };
    context.initInteraction = (...args) => { save = args[1]; originalInteraction(...args); };
    const layers = readFileSync(resolve(root, 'layers.js'), 'utf8');
    vm.runInContext(layers.slice(layers.indexOf('export function updateContainerSource')).replaceAll('export ', ''), context);
    const main = readFileSync(resolve(root, 'main.js'), 'utf8').replace(/^import[^\n]*;\s*$/gm, '')
        .replace(/init\(\)\.catch\([^\n]+\);/, 'globalThis.ready = init();');
    vm.runInContext(`(function () { ${main} })();`, context);
    await context.ready;
    return {
        rows, requests, scrolls, toast,
        activate: id => callbacks[0](rows.find(row => row.id === id)),
        select: id => callbacks[2](rows.find(row => row.id === id)),
        clear: id => callbacks[1](rows.find(row => row.id === id)),
        save: id => save(rows.find(row => row.id === id), feature()),
        measure: () => measure(),
        active: () => sources['container-active']?.features[0]?.properties.name ?? null,
        visible: id => sources.containers.features.some(item => item.properties.id === id),
        unplaced: id => sections['sidebar-unplaced'].cards.find(card => card.dataset.id === id),
        complete: async index => { requests[index].complete(); await flush(); },
    };
}

for (const placedB of [false, true]) {
    test(`late save preserves a newer ${placedB ? 'placed' : 'unplaced'} container selection`, async () => {
        const ui = await editor({ placedB });
        ui.activate('A'); const pending = ui.save('A');
        placedB ? ui.select('B') : ui.activate('B');
        await ui.complete(0); await pending;
        assert.equal(ui.active(), 'B');
        assert.equal(ui.visible('A'), true);
        assert.equal(ui.visible('B'), false);
        if (!placedB) assert.match(ui.unplaced('B').className, / active/);
        assert.deepEqual(ui.scrolls, []);
    });
}

test('late save refreshes the map without leaving measure mode', async () => {
    const ui = await editor(); ui.activate('A'); const pending = ui.save('A'); ui.measure();
    await ui.complete(0); await pending;
    assert.equal(ui.active(), null);
    assert.equal(ui.visible('A'), true);
    assert.deepEqual(ui.scrolls, []);
});

test('normal save keeps its active overlay and scrolls to the placed card', async () => {
    const ui = await editor(); ui.activate('A'); const pending = ui.save('A');
    await ui.complete(0); await pending;
    assert.equal(ui.active(), 'A');
    assert.equal(ui.visible('A'), false);
    assert.deepEqual(ui.scrolls, ['A']);
});

test('clearing the selected container makes its unplaced card pickable again', async () => {
    const ui = await editor({ placedA: true }); ui.select('A'); const pending = ui.clear('A');
    await ui.complete(0); await pending;
    assert.equal(ui.active(), null);
    assert.equal(ui.unplaced('A').tagName, 'button');
    assert.doesNotMatch(ui.unplaced('A').className, / active/);
});

test('late clear preserves a newer container selection', async () => {
    const ui = await editor({ placedA: true }); ui.select('A'); const pending = ui.clear('A'); ui.select('B');
    await ui.complete(0); await pending;
    assert.equal(ui.active(), 'B');
    assert.equal(ui.visible('B'), false);
    assert.equal(ui.rows[0].locationGeoJson, null);
});

for (const operation of ['save', 'clear']) {
    test(`failed ${operation} retains a newer selection and its saved state`, async () => {
        const ui = await editor({ placedA: operation === 'clear' });
        operation === 'save' ? ui.activate('A') : ui.select('A');
        const before = ui.rows[0].locationGeoJson, pending = ui[operation]('A'); ui.select('B');
        ui.requests[0].fail(new Error('Save failed')); await pending;
        assert.equal(ui.active(), 'B');
        assert.equal(ui.rows[0].locationGeoJson, before);
        assert.equal(ui.toast.textContent, 'Save failed');
    });
}
