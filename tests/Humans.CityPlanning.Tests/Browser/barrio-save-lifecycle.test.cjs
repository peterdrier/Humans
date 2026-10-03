const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.resolve(__dirname, '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map');
const main = fs.readFileSync(path.join(root, 'main.js'), 'utf8');
const edit = fs.readFileSync(path.join(root, 'edit.js'), 'utf8');
const handler = main.slice(main.indexOf("document.getElementById('save-btn')?.addEventListener"),
    main.indexOf("document.getElementById('cancel-btn')?.addEventListener"));
const update = edit.slice(edit.indexOf('export function updateSaveButton()'), edit.indexOf('// --- History ---'))
    .replace('export function', 'function');
const polygon = x => ({ type: 'Feature', geometry: { type: 'Polygon', coordinates: [[[x, 0], [2, 0], [2, 2], [x, 0]]] }, properties: {} });
function editor() {
    let save;
    const requests = [], alerts = [];
    const button = { disabled: false, dataset: {}, addEventListener: (_, callback) => save = callback };
    const state = { activeCampSeasonId: 'A', features: [polygon(0)], exits: 0,
        map: { getSource: () => ({ setData() {} }) } };
    state.draw = { getAll: () => ({ features: state.features }) };
    const context = {
        appState: state, CONFIG: { SAVE_FAILED: 'Localized save failure' },
        document: { getElementById: id => id === 'save-btn' ? button : { classList: { toggle() {} } },
            querySelector: () => ({ value: 'csrf' }) },
        turf: { area: () => 12, centroid: () => ({ geometry: { coordinates: [0, 0] } }),
            lineString: () => ({}), length: () => 1, midpoint: () => ({ properties: {} }), point: () => ({}) },
        isOutsideZone: () => false, overlapsOtherCamps: () => false,
        getSpaceRequirementSqm: () => null, getCampSoundZone: () => -1, getSoundZoneOutOfRange: () => false,
        clearDrawLabel() {}, alert: message => alerts.push(message), console: { error() {} },
        exitEditMode() { state.exits++; state.features = []; state.activeCampSeasonId = null; },
        fetch: (url, options) => new Promise((resolve, reject) => requests.push({ url, options, resolve, reject })),
    };
    vm.createContext(context);
    vm.runInContext(update + '\n' + handler, context);
    return { state, button, requests, alerts, save: () => save(), update: () => vm.runInContext('updateSaveButton()', context) };
}

test('late save keeps geometry edited after the request snapshot', async () => {
    const e = editor(), saving = e.save();
    const sent = JSON.parse(e.requests[0].options.body);
    e.state.features = [polygon(1)];
    e.requests[0].resolve({ ok: true });
    await saving;
    assert.equal(JSON.parse(sent.geoJson).geometry.coordinates[0][0][0], 0);
    assert.equal(e.state.features[0].geometry.coordinates[0][0][0], 1);
    assert.equal(e.state.exits, 0);
});

test('late save cannot close a different camp edit', async () => {
    const e = editor(), saving = e.save();
    e.state.activeCampSeasonId = 'B';
    e.state.features = [polygon(1)];
    e.requests[0].resolve({ ok: true });
    await saving;
    assert.equal(e.requests[0].url, '/api/city-planning/camp-polygons/A');
    assert.equal(e.state.activeCampSeasonId, 'B');
    assert.equal(e.state.exits, 0);
});

test('repeated save attempts cannot overlap writes', async () => {
    const e = editor(), first = e.save(), second = e.save();
    const count = e.requests.length;
    e.requests.forEach(r => r.resolve({ ok: false }));
    await Promise.all([first, second]);
    assert.equal(count, 1);
});

test('draw updates cannot re-enable Save while a write is pending', async () => {
    const e = editor(), saving = e.save();
    e.update();
    const disabled = e.button.disabled;
    e.requests[0].resolve({ ok: false });
    await saving;
    assert.equal(disabled, true);
    assert.equal(e.button.disabled, false);
});

test('unchanged successful save closes its edit', async () => {
    const e = editor(), saving = e.save();
    e.requests[0].resolve({ ok: true });
    await saving;
    assert.equal(e.state.exits, 1);
    assert.equal(e.state.activeCampSeasonId, null);
});

for (const network of [false, true]) {
    test(`${network ? 'network' : 'HTTP'} failure keeps edits and enables retry`, async () => {
        const e = editor(), saving = e.save();
        if (network) e.requests[0].reject(new Error('Offline'));
        else e.requests[0].resolve({ ok: false });
        await saving;
        assert.equal(e.state.activeCampSeasonId, 'A');
        assert.equal(e.state.exits, 0);
        assert.equal(e.button.disabled, false);
        assert.deepEqual(e.alerts, ['Localized save failure']);
    });
}
