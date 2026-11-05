const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const { installHumans } = require('../../browser-support/humans-js.cjs');

const source = readFileSync(resolve(__dirname, '../../../src/Sections/Humans.Shifts/Views/ShiftAdmin/Index.cshtml'), 'utf8');
const start = source.indexOf("    document.querySelectorAll('[data-new-tag-btn]')");
const script = source.slice(start, source.indexOf('\n})();', start));
const flush = () => new Promise(done => setImmediate(done));

function picker(id) {
    let click, keydown, complete, fail, requests = 0, syncs = 0;
    const errors = [];
    const checkbox = { checked: false };
    const input = {
        value: 'Heavy lifting', disabled: false,
        getAttribute: () => id,
        addEventListener: (_, handler) => { keydown = handler; },
    };
    const button = {
        disabled: false, getAttribute: () => id,
        addEventListener: (_, handler) => { click = handler; },
        click: () => { if (!button.disabled) click.call(button); },
    };
    const container = { querySelector: () => checkbox };
    const document = {
        querySelectorAll: selector => selector === '[data-new-tag-btn]' ? [button] : [input],
        querySelector: selector => selector.startsWith('[data-new-tag-input=') ? input
            : selector.startsWith('[data-new-tag-btn=') ? button : container,
    };
    vm.runInNewContext(script, installHumans({
        document, tagCreateUrl: '/tags/create',
        syncTagIds: pickerId => { assert.equal(pickerId, id); syncs++; },
        showToast: (message, type) => errors.push({ message, type }),
        fetch: (url, options) => {
            requests++;
            assert.equal(url, '/tags/create');
            assert.equal(options.method, 'POST');
            assert.equal(new URLSearchParams(options.body).get('name'), 'Heavy lifting');
            return new Promise((resolve, reject) => { complete = resolve; fail = reject; });
        },
    }));
    return {
        input, button, checkbox, errors, requests: () => requests, syncs: () => syncs,
        click: () => button.click(),
        enter: () => keydown.call(input, { key: 'Enter', preventDefault() {} }),
        complete: async ok => { complete({ ok, json: async () => ({ id: 'tag-id', name: 'Heavy lifting' }) }); await flush(); },
        fail: async () => { fail(new TypeError('offline')); await flush(); },
    };
}

for (const id of ['create', 'edit-rota']) {
    test(`${id}: refused tag save keeps the draft and allows retry`, async () => {
        const ui = picker(id);
        ui.click();
        await ui.complete(false);
        assert.equal(ui.input.value, 'Heavy lifting');
        assert.equal(ui.input.disabled, false);
        assert.equal(ui.button.disabled, false);
        assert.equal(ui.checkbox.checked, false);
        assert.equal(ui.syncs(), 0);
        assert.equal(ui.errors.length, 1);
        assert.equal(ui.errors[0].type, 'danger');
        assert.ok(ui.errors[0].message);
        ui.click();
        await ui.complete(true);
        assert.equal(ui.input.value, '');
        assert.equal(ui.checkbox.checked, true);
        assert.equal(ui.syncs(), 1);
    });

    test(`${id}: offline tag save reports failure without losing the draft`, async () => {
        const ui = picker(id);
        ui.click();
        await ui.fail();
        assert.equal(ui.input.value, 'Heavy lifting');
        assert.equal(ui.input.disabled, false);
        assert.equal(ui.button.disabled, false);
        assert.equal(ui.errors.length, 1);
        assert.equal(ui.errors[0].type, 'danger');
        assert.equal(ui.syncs(), 0);
    });

    test(`${id}: Enter cannot start another tag save while one is pending`, async () => {
        const ui = picker(id);
        ui.enter();
        assert.equal(ui.input.disabled, true);
        assert.equal(ui.button.disabled, true);
        ui.enter();
        assert.equal(ui.requests(), 1);
        await ui.complete(true);
        assert.equal(ui.input.disabled, false);
        assert.equal(ui.button.disabled, false);
        assert.equal(ui.checkbox.checked, true);
        assert.deepEqual(ui.errors, []);
    });
}
