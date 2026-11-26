const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const root = resolve(__dirname, '../../../src/Sections/Humans.CityPlanning');
const flush = () => new Promise(setImmediate);
const limit = JSON.stringify({ type: 'FeatureCollection', features: [] });

async function overview() {
    const requests = [], errors = [], layers = {};
    const view = readFileSync(resolve(root, 'Views/CityPlanning/Index.cshtml'), 'utf8');
    const buttons = {};
    let measureClicks = 0;
    for (const id of ['toggle-containers', 'toggle-camp-limits', 'measure-btn']) {
        const tag = view.match(new RegExp(`<button id="${id}"[^>]*>`))?.[0] ?? '';
        buttons[id] = { disabled: /\sdisabled(?:\s|>|=)/.test(tag), attributes: {},
            classList: { toggle() {} }, setAttribute(name, value) { this.attributes[name] = value; },
            addEventListener(event, handler) { this[event] = handler; } };
    }
    const map = {
        on: (event, handler) => { if (event === 'load') queueMicrotask(handler); },
        addSource() {}, addLayer: layer => { layers[layer.id] = layer; }, getLayer: id => layers[id],
        setLayoutProperty: (id, name, value) => { (layers[id].layout ??= {})[name] = value; },
    };
    const context = {
        CONFIG: { YEAR: 2026, MAP_BOUNDS: [], ESRI_TILES: [] },
        document: { documentElement: {}, getElementById: id => buttons[id] },
        getComputedStyle: () => ({ getPropertyValue: () => '' }),
        maplibregl: { Map: function () { return map; } },
        initMeasure() {}, wireMeasureButtons: () => buttons['measure-btn'].addEventListener('click', () => measureClicks++),
        addOfficialZonesLayers() {}, SOUND_ZONE_FILL_EXPR: [], SOUND_ZONE_LINE_EXPR: [],
        fetch: url => new Promise((complete, fail) => requests.push({ url, complete, fail })),
        console: { error: (...args) => errors.push(args) },
    };
    vm.createContext(context);
    const script = file => readFileSync(resolve(root, 'wwwroot/js/city-planning', file), 'utf8')
        .replace(/^import[^\n]*;\s*$/gm, '').replaceAll('export ', '');
    vm.runInContext(script('container-map/api.js'), context);
    vm.runInContext(script('main.js').replace('init().catch', 'globalThis.finished = init().catch'), context);
    await flush();
    return {
        requests, errors, layers, buttons, measureClicks: () => measureClicks,
        click(id) { if (!buttons[id].disabled) buttons[id].click?.(); },
        async state(data = { limitZoneGeoJson: limit, campPolygons: [] }) {
            requests[0].complete({ ok: true, json: async () => data }); await flush();
        },
        async containers() { requests[1].complete({ ok: true, json: async () => [] }); await context.finished; },
        finished: () => context.finished,
    };
}

test('the ruler works while the state request is pending', async () => {
    const ui = await overview(); ui.click('measure-btn');
    assert.equal(ui.measureClicks(), 1);
    ui.requests[0].fail(new Error('Offline')); await ui.finished();
});

test('state failure leaves the ruler usable and unloaded layers disabled', async () => {
    const ui = await overview(); ui.requests[0].fail(new Error('Offline')); await ui.finished();
    ui.click('measure-btn'); assert.equal(ui.measureClicks(), 1);
    assert.equal(ui.buttons['toggle-containers'].disabled, true);
    assert.equal(ui.buttons['toggle-camp-limits'].disabled, true);
});

test('HTTP state failure is rejected before its JSON can resemble empty map data', async () => {
    const ui = await overview(); let decoded = false;
    ui.requests[0].complete({ ok: false, status: 503, json: async () => { decoded = true; return {}; } });
    await flush();
    // Drain a request made by the old implementation before asserting the refusal.
    if (ui.requests[1]) ui.requests[1].fail(new Error('Offline'));
    await ui.finished();
    assert.equal(decoded, false);
    assert.equal(ui.requests.length, 1);
});

test('loaded limits work while containers are still loading', async () => {
    const ui = await overview(); await ui.state(); ui.click('toggle-camp-limits');
    assert.equal(ui.layers['limit-zone-line'].layout.visibility, 'visible');
    assert.equal(ui.buttons['toggle-containers'].disabled, true);
    ui.requests[1].fail(new Error('Offline')); await ui.finished();
});

for (const failure of ['network', 'JSON', 'HTTP']) {
    test(`container ${failure} failure preserves loaded tools`, async () => {
        const ui = await overview(); await ui.state();
        if (failure === 'network') ui.requests[1].fail(new Error('Offline'));
        else ui.requests[1].complete({ ok: failure !== 'HTTP', status: 503,
            json: async () => { throw new Error('Invalid JSON'); } });
        await ui.finished();
        ui.click('measure-btn'); ui.click('toggle-camp-limits');
        assert.equal(ui.measureClicks(), 1);
        assert.equal(ui.layers['limit-zone-line'].layout.visibility, 'visible');
        assert.equal(ui.buttons['toggle-containers'].disabled, true);
        assert.equal(ui.errors.length, 1);
    });
}

test('successful loads enable both independent layer toggles', async () => {
    const ui = await overview(); await ui.state(); await ui.containers();
    ui.click('toggle-containers'); ui.click('toggle-camp-limits');
    assert.equal(ui.layers['containers-fill'].layout.visibility, 'visible');
    assert.equal(ui.layers['containers-labels'].layout.visibility, 'visible');
    assert.equal(ui.layers['limit-zone-line'].layout.visibility, 'visible');
    assert.equal(ui.buttons['toggle-containers'].attributes['aria-pressed'], 'true');
    ui.click('toggle-containers');
    assert.equal(ui.layers['containers-fill'].layout.visibility, 'none');
});

test('an overview without limits keeps only that toggle disabled', async () => {
    const ui = await overview(); await ui.state({ campPolygons: [] }); await ui.containers();
    assert.equal(ui.buttons['toggle-camp-limits'].disabled, true);
    assert.equal(ui.buttons['toggle-containers'].disabled, false);
});
