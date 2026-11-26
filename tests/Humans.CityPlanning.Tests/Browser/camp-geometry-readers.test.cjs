const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync, existsSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const root = resolve(__dirname, '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning');
const ring = [[0, 0, 3], [4, 0, 3], [4, 4, 3], [0, 0, 3]];
const area = type => ({ type, coordinates: type === 'Polygon' ? [ring] : [[ring], [ring]] });
const flush = () => new Promise(setImmediate);

function client(geoJson) {
    const sources = {}, previews = [], drawn = [];
    const elements = {};
    const element = id => elements[id] ??= { dataset: {}, disabled: false, style: {},
        classList: { toggle() {}, add() {}, remove() {}, contains: () => false },
        addEventListener() {}, setAttribute() {} };
    const list = element('history-list');
    list.querySelectorAll = selector => selector !== '.preview-btn' ? [] :
        [...list.innerHTML.matchAll(/data-id="([^"]+)" data-geojson="([^"]+)"/g)].map(match => ({
            dataset: { id: match[1], geojson: match[2] }, addEventListener: (_, handler) => previews.push(handler),
        }));
    const row = { campSeasonId: 'A', campName: 'Camp A', campSlug: 'camp-a', soundZone: 0, areaSqm: 10, geoJson };
    const state = { campMap: { campPolygons: [row] }, activeCampSeasonId: null,
        draw: { features: [], getAll() { return { features: this.features }; },
            deleteAll() { this.features = []; }, add(f) { drawn.push(f); this.features.push(f); }, changeMode() {} } };
    const map = { on: (event, handler) => { if (event === 'load') queueMicrotask(handler); },
        addSource: (id, source) => { sources[id] = source.data; }, addLayer() {},
        getSource: id => ({ setData: data => { sources[id] = data; } }) };
    state.map = map;
    const context = {
        appState: state, CONFIG: { YEAR: 2026, USER_CAMP_SEASON_ID: 'A', HISTORY_FOR_CAMP: '{0}', PREVIEW: 'Preview' },
        SOUND_ZONE_NAMES: {}, SOUND_ZONE_FILL_EXPR: [], SOUND_ZONE_LINE_EXPR: [],
        document: { documentElement: {}, getElementById: element, createElement: () => ({ textContent: '' }) },
        maplibregl: { Map: function () { return map; } },
        getComputedStyle: () => ({ getPropertyValue: () => '' }),
        initMeasure() {}, wireMeasureButtons() {}, addOfficialZonesLayers() {}, setActivePolygonDim() {},
        loadContainers: async () => [],
        bootstrap: { Offcanvas: { getOrCreateInstance: () => ({ show() {} }) } },
        fetch: async url => ({ ok: true, json: async () => url.endsWith('/history')
            ? [{ id: 'version-A', geoJson, modifiedByDisplayName: 'Person', modifiedAt: 'Now', areaSqm: 10, note: '' }]
            : { campPolygons: [row] } }),
        Humans: { escapeHtml: value => String(value ?? '') }, console,
        turf: {
            area: () => 10, centroid: () => ({ type: 'Feature', geometry: { type: 'Point', coordinates: [0, 0] } }),
            lineString: () => ({}), length: () => 4, point: value => value, midpoint: () => ({ properties: {} }),
            featureCollection: features => ({ type: 'FeatureCollection', features }),
            intersect: collection => {
                assert.ok(collection.features.every(f => f.type === 'Feature'));
                return { type: 'Feature' };
            },
        },
    };
    vm.createContext(context);
    function load(file) {
        const source = readFileSync(resolve(root, file), 'utf8')
            .replace(/^import[^\n]*;\s*$/gm, '').replaceAll('export ', '');
        vm.runInContext(source, context);
    }
    if (existsSync(resolve(root, 'shared/camp-geometry.js'))) load('shared/camp-geometry.js');
    load('barrio-map/geometry.js');
    return { context, state, row, sources, drawn, previews, load,
        feature: () => sources['camp-polygons'].features[0] };
}

for (const type of ['Polygon', 'MultiPolygon']) {
    for (const wrapped of [false, true]) {
        const geometry = area(type);
        const stored = wrapped ? { type: 'Feature', id: 'existing-id', properties: { custom: 'retained' }, geometry } : geometry;
        const geoJson = JSON.stringify(stored);
        const label = `${wrapped ? 'Feature-wrapped' : 'bare'} ${type}`;
        function verify(f) {
            assert.equal(f.type, 'Feature');
            assert.deepEqual(JSON.parse(JSON.stringify(f.geometry)), geometry);
            if (wrapped) assert.equal(f.properties.custom, 'retained');
        }

        test(`${label} renders in the barrio map`, () => {
            const ui = client(geoJson);
            const f = ui.context.buildCampPolygonFeatures([ui.row])[0];
            verify(f); assert.equal(f.properties.campSlug, 'camp-a');
        });

        test(`${label} renders in the container map background`, () => {
            const ui = client(geoJson); ui.load('container-map/layers.js');
            ui.context.addBackgroundLayers(ui.state.map, ui.state.campMap);
            verify(ui.feature()); assert.equal(ui.feature().properties.campName, 'Camp A');
        });

        test(`${label} renders in the overview`, async () => {
            const ui = client(geoJson); ui.load('main.js'); await flush();
            verify(ui.feature()); assert.equal(ui.feature().properties.campName, 'Camp A');
        });

        test(`${label} starts an edit with a Feature identifier`, () => {
            const ui = client(geoJson); ui.load('barrio-map/edit.js'); ui.context.startEditing('A');
            verify(ui.drawn[0]); assert.equal(ui.drawn[0].id, wrapped ? 'existing-id' : 'active-polygon');
        });

        test(`${label} previews its history as a Feature`, async () => {
            const ui = client(geoJson); ui.load('barrio-map/edit.js');
            await ui.context.loadHistory('A'); ui.previews[0](); verify(ui.drawn[0]);
        });

        test(`${label} participates in overlap checks`, () => {
            const ui = client(geoJson);
            assert.equal(ui.context.overlapsOtherCamps({ type: 'Feature', geometry: area('Polygon') }), true);
        });
    }
}
