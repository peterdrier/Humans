const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const source = readFileSync(resolve(__dirname,
    '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/signalr.js'), 'utf8')
    .replace(/^import[^\n]*;\s*$/gm, '').replaceAll('export ', '');

function deferred() {
    let resolve, reject;
    const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
    return { promise, resolve, reject };
}

function client() {
    const events = {}, requests = [], rendered = [], toasts = [], errors = [];
    const start = deferred();
    let reconnect;
    let controls = 0;
    const draft = { geometry: 'unsaved draft' };
    const state = {
        campMap: { campPolygons: [{ campSeasonId: 'A', geoJson: 'old', areaSqm: 10 }],
            campSeasonsWithoutPolygon: [], limitZoneGeoJson: 'original zones' },
        activeCampSeasonId: 'A', previewCampSeasonId: 'preview', draw: draft,
        map: { getSource: () => ({ setData: data => rendered.push(data) }) },
    };
    const connection = {
        on: (event, callback) => { events[event] = callback; },
        onreconnected: callback => { reconnect = callback; }, start: () => start.promise,
    };
    const context = {
        appState: state, CONFIG: { MAP_REFRESH_FAILED: 'localized refresh failure' },
        signalR: { HubConnectionBuilder: function () {
            this.withUrl = this.withAutomaticReconnect = () => this; this.build = () => connection;
        } },
        fetch: (url, options) => { const result = deferred(); requests.push({ url, options, ...result }); return result.promise; },
        buildCampPolygonFeatures: rows => rows.map(row => ({ ...row })), invalidateParsedFeature() {},
        updateAddControls: () => controls++, showToast: (...args) => toasts.push(args),
        console: { error: (...args) => errors.push(args) },
    };
    vm.runInNewContext(source, context);
    context.initSignalR();
    return { state, draft, start, requests, rendered, toasts, errors,
        reconnect: () => reconnect?.(), controls: () => controls,
        broadcast: (id, geometry, area) => events.CampPolygonUpdated(id, geometry, area, 2, 'Live name'),
    };
}

function snapshot(geometry = 'fresh') {
    return { campPolygons: [{ campSeasonId: 'A', geoJson: geometry, areaSqm: 20,
        campSlug: 'camp-a', spaceRequirementSqm: 100 }],
    campSeasonsWithoutPolygon: [{ campSeasonId: 'B', campSlug: 'camp-b', spaceRequirementSqm: 200 }] };
}

function respond(request, value = snapshot()) {
    request.resolve({ ok: true, json: async () => value });
}

test('reconnect replaces placements missed while disconnected', async () => {
    const ui = client();
    const refreshed = ui.reconnect();
    assert.equal(ui.requests.length, 1);
    assert.equal(ui.requests[0].url, '/api/city-planning/state');
    respond(ui.requests[0]); await refreshed;
    assert.equal(ui.state.campMap.campPolygons[0].geoJson, 'fresh');
    assert.equal(ui.state.campMap.campPolygons[0].campSlug, 'camp-a');
    assert.equal(ui.state.campMap.campSeasonsWithoutPolygon[0].campSeasonId, 'B');
    assert.equal(ui.rendered.at(-1).features[0].geoJson, 'fresh');
    assert.equal(ui.controls(), 1);
    assert.equal(ui.toasts.length, 0);
});

test('initial connection refresh closes the snapshot to connection gap', async () => {
    const ui = client(); ui.start.resolve();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(ui.requests.length, 1);
    respond(ui.requests[0]);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(ui.state.campMap.campPolygons[0].geoJson, 'fresh');
});

test('refresh retains the active editing session', async () => {
    const ui = client(); const refreshed = ui.reconnect();
    assert.equal(ui.requests.length, 1);
    respond(ui.requests[0]); await refreshed;
    assert.equal(ui.state.draw, ui.draft);
    assert.equal(ui.draft.geometry, 'unsaved draft');
    assert.equal(ui.state.activeCampSeasonId, 'A');
    assert.equal(ui.state.previewCampSeasonId, 'preview');
    assert.equal(ui.state.campMap.limitZoneGeoJson, 'original zones');
});

test('broadcasts during refresh override older snapshot geometry', async () => {
    const ui = client(); const refreshed = ui.reconnect();
    assert.equal(ui.requests.length, 1);
    ui.broadcast('A', 'earlier live', 25); ui.broadcast('A', 'live', 30);
    respond(ui.requests[0]); await refreshed;
    const row = ui.state.campMap.campPolygons[0];
    assert.equal(row.geoJson, 'live'); assert.equal(row.areaSqm, 30);
    assert.equal(row.campSlug, 'camp-a'); assert.equal(row.spaceRequirementSqm, 100);
    assert.equal(ui.rendered.at(-1).features[0].geoJson, 'live');
});

test('new placements during refresh retain snapshot season metadata', async () => {
    const ui = client(); const refreshed = ui.reconnect();
    assert.equal(ui.requests.length, 1);
    ui.broadcast('B', 'new live polygon', 50);
    respond(ui.requests[0]); await refreshed;
    const row = ui.state.campMap.campPolygons.find(row => row.campSeasonId === 'B');
    assert.equal(row.geoJson, 'new live polygon'); assert.equal(row.areaSqm, 50);
    assert.equal(row.campSlug, 'camp-b'); assert.equal(row.spaceRequirementSqm, 200);
    assert.equal(row.soundZone, 2); assert.equal(row.campName, 'Live name');
});

test('broadcasts received while decoding the snapshot are retained', async () => {
    const ui = client(); const body = deferred(); const refreshed = ui.reconnect();
    assert.equal(ui.requests.length, 1);
    ui.requests[0].resolve({ ok: true, json: () => body.promise });
    await Promise.resolve(); ui.broadcast('A', 'newest', 40);
    body.resolve(snapshot()); await refreshed;
    assert.equal(ui.state.campMap.campPolygons[0].geoJson, 'newest');
});

test('a newer reconnect supersedes an older snapshot request', async () => {
    const ui = client(); const first = ui.reconnect(); const second = ui.reconnect();
    assert.equal(ui.requests.length, 2);
    respond(ui.requests[1], snapshot('second')); await second;
    respond(ui.requests[0], snapshot('first')); await first;
    assert.equal(ui.state.campMap.campPolygons[0].geoJson, 'second');
    assert.equal(ui.controls(), 1);
});

for (const failure of ['http', 'network', 'json']) {
    test(`${failure} refresh failure reports localized feedback without replacing placements`, async () => {
        const ui = client(); const refreshed = ui.reconnect();
        assert.equal(ui.requests.length, 1);
        if (failure === 'network') ui.requests[0].reject(new Error('offline'));
        else ui.requests[0].resolve({ ok: failure !== 'http', status: 503,
            json: async () => { throw new Error('invalid json'); } });
        await refreshed;
        assert.equal(ui.state.campMap.campPolygons[0].geoJson, 'old');
        assert.equal(ui.rendered.length, 0);
        assert.deepEqual(ui.toasts, [['localized refresh failure', 'danger']]);
        assert.equal(ui.errors.length, 1);
    });
}

test('an obsolete failed request cannot report failure after a newer refresh', async () => {
    const ui = client(); const first = ui.reconnect(); const second = ui.reconnect();
    assert.equal(ui.requests.length, 2);
    respond(ui.requests[1]); await second;
    ui.requests[0].reject(new Error('obsolete failure')); await first;
    assert.equal(ui.toasts.length, 0); assert.equal(ui.errors.length, 0);
});
