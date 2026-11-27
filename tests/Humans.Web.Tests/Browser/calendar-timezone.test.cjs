const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const source = readFileSync(resolve(__dirname, '../../../src/Sections/Humans.Calendar/Views/Calendar/_CalendarTimeZonePicker.cshtml'), 'utf8');
const script = source.slice(source.indexOf('<script '), source.indexOf('</script>')).replace(/^[^\n]*\n/, '');
const flush = () => new Promise(done => setImmediate(done));

function picker() {
    let change, complete, fail, requests = 0, reloads = 0;
    const errors = [];
    const element = {
        value: 'Europe/Madrid', disabled: false, dataset: { error: 'Update failed' },
        addEventListener: (_, handler) => { change = handler; },
    };
    vm.runInNewContext(script, {
        document: { getElementById: () => element },
        location: { reload: () => { reloads++; } },
        showToast: (message, type) => errors.push({ message, type }),
        fetch: (url, options) => {
            requests++;
            assert.equal(url, '/api/timezone');
            assert.equal(options.method, 'POST');
            assert.equal(JSON.parse(options.body).timeZone, element.value);
            return new Promise((resolve, reject) => { complete = resolve; fail = reject; });
        },
    });
    return {
        element, errors, reloads: () => reloads, requests: () => requests,
        change: zone => { element.value = zone; change.call(element); },
        complete: async ok => { complete({ ok, status: ok ? 200 : 503 }); await flush(); },
        fail: async () => { fail(new TypeError('offline')); await flush(); },
    };
}

test('refused timezone save restores the displayed zone and allows retry', async () => {
    const ui = picker();
    ui.change('America/New_York');
    await ui.complete(false);
    assert.equal(ui.element.value, 'Europe/Madrid');
    assert.equal(ui.element.disabled, false);
    assert.equal(ui.reloads(), 0);
    assert.deepEqual(ui.errors, [{ message: 'Update failed', type: 'danger' }]);
    ui.change('Europe/Paris');
    await ui.complete(true);
    assert.equal(ui.reloads(), 1);
});

test('offline timezone save restores the displayed zone and reports failure', async () => {
    const ui = picker();
    ui.change('America/New_York');
    await ui.fail();
    assert.equal(ui.element.value, 'Europe/Madrid');
    assert.equal(ui.element.disabled, false);
    assert.equal(ui.reloads(), 0);
    assert.deepEqual(ui.errors, [{ message: 'Update failed', type: 'danger' }]);
});

test('timezone picker blocks overlapping saves and reloads after success', async () => {
    const ui = picker();
    ui.change('America/New_York');
    assert.equal(ui.element.disabled, true);
    ui.change('Europe/Paris');
    assert.equal(ui.requests(), 1);
    await ui.complete(true);
    assert.equal(ui.reloads(), 1);
    assert.deepEqual(ui.errors, []);
});

const site = readFileSync(resolve(__dirname, '../../../src/Humans.Web/wwwroot/js/site.js'), 'utf8');
const automaticScript = site.slice(site.indexOf('// Timezone detection'), site.indexOf('// Notification bell popup'));
for (const outcome of ['success', 'refused', 'offline']) {
    test(`automatic timezone reporting handles ${outcome}`, async () => {
        const warnings = [], requests = [];
        vm.runInNewContext(automaticScript, {
            Intl: { DateTimeFormat: () => ({ resolvedOptions: () => ({ timeZone: 'Europe/Madrid' }) }) },
            document: { querySelector: () => ({}) },
            console: { warn: (...args) => warnings.push(args) },
            fetch: (url, options) => {
                requests.push({ url, options });
                return outcome === 'offline' ? Promise.reject(new Error('offline'))
                    : Promise.resolve({ ok: outcome === 'success', status: outcome === 'success' ? 204 : 503 });
            },
        });
        await flush();
        assert.equal(requests.length, 1);
        assert.equal(requests[0].url, '/api/timezone');
        assert.equal(requests[0].options.method, 'POST');
        assert.equal(JSON.parse(requests[0].options.body).timeZone, 'Europe/Madrid');
        assert.equal(warnings.length, outcome === 'success' ? 0 : 1);
    });
}
