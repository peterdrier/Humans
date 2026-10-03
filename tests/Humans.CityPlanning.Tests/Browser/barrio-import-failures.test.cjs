const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const script = fs.readFileSync(path.resolve(__dirname,
    '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/admin-import.js'), 'utf8');
function importer(outcomes) {
    const writes = [], states = [];
    let hidden = false;
    const result = { textContent: '', className: '', classList: { remove() {} } };
    const confirm = { disabled: false, addEventListener() {} };
    const fileInput = { value: 'upload.geojson' };
    const elements = {
        'import-file-input': fileInput, 'import-preview-btn': { addEventListener() {} },
        'import-error': {}, 'import-strings': { dataset: {
            updating: 'Updating {0}', importSuccess: 'Updated {0}', importPartialFailure: 'Updated {0}; failed {1}: {2}',
        } },
        'import-confirm-btn': confirm, 'import-status': { textContent: '', classList: { remove() {}, toggle() {} } },
        'import-preview-modal': {}, 'import-result': result,
    };
    const context = {
        console: { error() {} },
        document: { getElementById: id => elements[id], querySelector: () => ({ value: 'csrf' }) },
        bootstrap: { Modal: { getInstance: () => ({ hide: () => hidden = true }) } },
        fetch: (url, options) => {
            states.push(confirm.disabled);
            writes.push({ url, body: JSON.parse(options.body) });
            const outcome = outcomes[writes.length - 1];
            return outcome === 'network' ? Promise.reject(new Error('Offline')) : Promise.resolve({ ok: outcome === 'ok' });
        },
    };
    vm.createContext(context);
    vm.runInContext(script + '\npendingImport = { matched: [\n' +
        outcomes.map((_, index) => `{ campSeasonId: '${index}', campName: 'Camp ${index}', geoJson: 'polygon-${index}', newAreaSqm: 42 }`).join(',\n') +
        '\n] };', context);
    return { result, confirm, fileInput, writes, states, hidden: () => hidden,
        run: () => vm.runInContext('handleConfirm()', context),
        pending: () => vm.runInContext('pendingImport', context) };
}

for (const outcomes of [['ok', 'network', 'ok'], ['network', 'network']]) {
    test(`network failures are included in the final report: ${outcomes.join(',')}`, async () => {
        const i = importer(outcomes);
        await i.run();
        const successes = outcomes.filter(o => o === 'ok').length;
        const failures = outcomes.map((o, index) => o === 'network' ? `Camp ${index}` : null).filter(Boolean);
        assert.equal(i.writes.length, outcomes.length);
        assert.equal(i.result.textContent, `Updated ${successes}; failed ${failures.length}: ${failures.join(', ')}`);
        assert.equal(i.result.className, 'alert alert-warning mt-2');
        assert.equal(i.confirm.disabled, false);
        assert.equal(i.hidden(), true);
        assert.equal(i.pending(), null);
        assert.equal(i.fileInput.value, '');
        assert.ok(i.states.every(Boolean));
    });
}

test('HTTP failures continue to produce the same partial result', async () => {
    const i = importer(['ok', 'http', 'ok']);
    await i.run();
    assert.equal(i.result.textContent, 'Updated 2; failed 1: Camp 1');
    assert.equal(i.writes[2].url, '/api/city-planning/camp-polygons/2');
    assert.equal(i.writes[2].body.geoJson, 'polygon-2');
});

test('successful import reports success and unlocks confirmation', async () => {
    const i = importer(['ok', 'ok']);
    await i.run();
    assert.equal(i.result.textContent, 'Updated 2');
    assert.equal(i.result.className, 'alert alert-success mt-2');
    assert.equal(i.confirm.disabled, false);
});
