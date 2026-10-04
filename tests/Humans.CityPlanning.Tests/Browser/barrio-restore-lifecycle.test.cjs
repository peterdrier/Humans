const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.resolve(__dirname,
    '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/edit.js'), 'utf8')
    .replace(/^import[^\n]*;\s*$/gm, '').replaceAll('export ', '');
function editor() {
    let hides = 0, clears = 0;
    const requests = [], alerts = [], events = {};
    const buttons = [{ disabled: false }, { disabled: false }];
    const panel = { dataset: {}, addEventListener: (event, handler) => events[event] = handler };
    const list = { querySelectorAll: () => buttons };
    const state = { campMap: { campPolygons: [] }, activeCampSeasonId: null, features: [{ id: 'old-preview' }],
        map: { getSource: () => ({ setData() {} }) } };
    state.draw = { getAll: () => ({ features: state.features }), deleteAll() { clears++; state.features = []; } };
    const context = {
        appState: state, CONFIG: {}, setActivePolygonDim() {},
        document: { getElementById: id => id === 'history-panel' ? panel : id === 'history-list' ? list :
            { disabled: false, classList: { add() {}, remove() {}, toggle() {}, contains: () => false } }, querySelector: () => ({ value: 'csrf' }) },
        bootstrap: { Offcanvas: { getInstance: () => ({ hide: () => hides++ }) } },
        confirm: () => true, alert: message => alerts.push(message), console: { error() {} },
        fetch: (url, options) => new Promise((resolve, reject) => requests.push({ url, options, resolve, reject })),
    };
    vm.createContext(context); vm.runInContext(source, context);
    return { state, buttons, requests, alerts, hides: () => hides, clears: () => clears,
        restore: () => context.restoreVersion('version', 'A'),
        newHistory: () => vm.runInContext('historyVersion++', context),
        dismiss: () => events['hide.bs.offcanvas']() };
}

for (const change of ['camp', 'geometry', 'history', 'dismiss']) {
    test(`late restore cannot clear newer ${change} state`, async () => {
        const e = editor(), restoring = e.restore();
        if (change === 'camp') e.state.activeCampSeasonId = 'B';
        if (change === 'geometry') e.state.features = [{ id: 'new-preview' }];
        if (change === 'history') e.newHistory();
        if (change === 'dismiss') e.dismiss();
        e.requests[0].resolve({ ok: true });
        await restoring;
        assert.equal(e.hides(), 0);
        assert.equal(e.clears(), 0);
        assert.equal(e.requests[0].url, '/api/city-planning/camp-polygons/A/restore/version');
    });
}

test('unchanged restore closes its panel and clears its old preview', async () => {
    const e = editor(), restoring = e.restore();
    e.requests[0].resolve({ ok: true });
    await restoring;
    assert.equal(e.hides(), 1);
    assert.equal(e.clears(), 1);
});

test('restore requests cannot overlap and all restore controls unlock after failure', async () => {
    const e = editor(), first = e.restore();
    const disabled = e.buttons.every(button => button.disabled);
    const second = e.restore();
    const count = e.requests.length;
    e.requests.forEach(request => request.resolve({ ok: false }));
    await Promise.all([first, second]);
    assert.equal(disabled, true);
    assert.equal(count, 1);
    assert.ok(e.buttons.every(button => !button.disabled));
    assert.deepEqual(e.alerts, ['Restore failed.']);
    assert.equal(e.clears(), 0);
});

test('network failure unlocks restore controls and keeps the draft', async () => {
    const e = editor(), restoring = e.restore();
    e.requests[0].reject(new Error('Offline'));
    await restoring;
    assert.equal(e.clears(), 0);
    assert.equal(e.hides(), 0);
    assert.ok(e.buttons.every(button => !button.disabled));
    assert.deepEqual(e.alerts, ['Restore failed.']);
});
