const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const script = fs.readFileSync(path.resolve(__dirname,
    '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/admin-import.js'), 'utf8');
const settle = () => new Promise(setImmediate);
function deferred() {
    let resolve, reject;
    const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
    return { promise, resolve, reject };
}
function preview() {
    const read = deferred(), response = deferred();
    let shows = 0, fetches = 0;
    const classes = { add() {}, remove() {}, toggle() {} };
    const fileInput = { files: [{ size: 12, text: () => read.promise }] };
    const error = { textContent: '', classList: classes };
    const elements = {
        'import-file-input': fileInput, 'import-preview-btn': { disabled: false, addEventListener() {} },
        'import-error': error, 'import-strings': { dataset: { invalidJsonError: 'Invalid JSON', fetchStateError: 'State unavailable' } },
        'import-confirm-btn': { disabled: false, addEventListener() {} },
        'import-status': { textContent: '', classList: classes },
        'import-matched-body': { innerHTML: '' }, 'import-unrecognized-section': { classList: classes },
        'import-unrecognized-list': { innerHTML: '' }, 'import-preview-modal': {},
    };
    const context = {
        console: { error() {}, warn() {} }, turf: { area: () => 12 },
        document: { getElementById: id => elements[id],
            createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } }) },
        bootstrap: { Modal: { getOrCreateInstance: () => ({ show: () => shows++ }) } },
        fetch: () => { fetches++; return response.promise; },
    };
    vm.createContext(context); vm.runInContext(script, context);
    return { read, response, fileInput, error, shows: () => shows, fetches: () => fetches,
        pending: () => vm.runInContext('pendingImport', context), run: () => vm.runInContext('handlePreview()', context),
        changeFile: () => fileInput.files = [{ size: 15, text: () => Promise.resolve('different-file') }],
        button: elements['import-preview-btn'] };
}
const json = JSON.stringify({ type: 'FeatureCollection', features: [
    { type: 'Feature', properties: { campName: 'Alpha' }, geometry: { type: 'Polygon', coordinates: [] } },
] });
const stateResponse = () => ({ ok: true, json: () => Promise.resolve({ campPolygons: [
    { campSeasonId: 'A', campName: 'Alpha', campSlug: 'alpha', areaSqm: 10 },
] }) });

for (const invalid of [false, true]) {
    test(`changing files during ${invalid ? 'invalid' : 'valid'} file reading discards the old preview`, async () => {
        const p = preview(), loading = p.run();
        p.changeFile(); p.read.resolve(invalid ? '{invalid' : json);
        // Resolve in case the old implementation continues into the state request.
        p.response.resolve(stateResponse());
        await loading;
        assert.equal(p.shows(), 0);
        assert.equal(p.fetches(), 0);
        assert.equal(p.error.textContent, '');
        assert.equal(p.pending(), null);
        assert.equal(p.button.disabled, false);
    });
}

for (const failed of [false, true]) {
    test(`changing files during state ${failed ? 'failure' : 'success'} discards the old preview`, async () => {
        const p = preview(), loading = p.run();
        p.read.resolve(json); await settle();
        assert.equal(p.fetches(), 1);
        p.changeFile();
        if (failed) p.response.reject(new Error('Offline'));
        else p.response.resolve(stateResponse());
        await loading;
        assert.equal(p.shows(), 0);
        assert.equal(p.error.textContent, '');
        assert.equal(p.pending(), null);
        assert.equal(p.button.disabled, false);
    });
}

test('unchanged selected file still produces its reviewed match', async () => {
    const p = preview(), loading = p.run();
    p.read.resolve(json); p.response.resolve(stateResponse());
    await loading;
    assert.equal(p.shows(), 1);
    assert.equal(p.pending().matched[0].campSeasonId, 'A');
    assert.equal(p.pending().matched[0].newAreaSqm, 12);
    assert.equal(p.button.disabled, false);
});
