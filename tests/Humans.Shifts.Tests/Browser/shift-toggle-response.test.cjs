const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const script = fs.readFileSync(path.resolve(__dirname,
    '../../../src/Sections/Humans.Shifts/wwwroot/js/shifts.js'), 'utf8');

async function toggle(status, headers = {}, reject = false) {
    let click;
    const state = { removed: false, descriptionRemoved: false, inserted: null, focused: false, toasts: [], reads: 0 };
    const description = { querySelector: () => ({}), remove: () => state.descriptionRemoved = true };
    const newRow = { querySelector: () => ({ disabled: false, focus: () => state.focused = true }) };
    const row = {
        nextElementSibling: description,
        insertAdjacentHTML(position, html) { state.inserted = html; this.nextElementSibling = newRow; },
        remove: () => state.removed = true,
    };
    const button = { disabled: false, innerHTML: 'Sign up', dataset: { shiftId: 'shift-id' }, closest: () => row };
    const container = {
        getAttribute: () => 'Localized network error',
        appendChild: element => state.toasts.push(element.textContent),
    };
    const window = { location: null };
    const document = {
        addEventListener: (name, handler) => click = handler,
        querySelector: selector => selector.startsWith('input') ? { value: 'csrf' } : container,
        querySelectorAll: () => [],
        createElement: () => ({ remove() {} }),
    };
    vm.runInNewContext(script, {
        document, window, FormData: class { append() {} }, setTimeout() {},
        fetch: () => reject ? Promise.reject(new Error('Network down')) : Promise.resolve({
            ok: status >= 200 && status < 300, status,
            headers: { get: name => headers[name] ?? null },
            text: () => { state.reads++; return Promise.resolve('<tr>server fragment</tr>'); },
        }),
    });
    click({ target: { closest: () => button } });
    await new Promise(setImmediate);
    return { state, button, window };
}

for (const status of [400, 403, 500]) {
    test(`HTTP ${status} keeps the row and allows retry`, async () => {
        const { state, button } = await toggle(status);
        assert.equal(state.removed, false);
        assert.equal(state.descriptionRemoved, false);
        assert.equal(state.inserted, null);
        assert.equal(state.reads, 0);
        assert.equal(button.disabled, false);
        assert.equal(button.innerHTML, 'Sign up');
        assert.deepEqual(state.toasts, ['Localized network error']);
    });
}

test('successful response replaces the row and restores keyboard focus', async () => {
    const { state } = await toggle(200);
    assert.equal(state.removed, true);
    assert.equal(state.descriptionRemoved, true);
    assert.equal(state.inserted, '<tr>server fragment</tr>');
    assert.equal(state.focused, true);
    assert.deepEqual(state.toasts, []);
});

test('successful warning response still renders its updated row', async () => {
    const { state } = await toggle(200, { 'X-Toast-Type': 'warning', 'X-Toast-Msg': encodeURIComponent('Localized warning') });
    assert.equal(state.removed, true);
    assert.deepEqual(state.toasts, ['Localized warning']);
});

test('redirect header navigates without consuming or replacing the row', async () => {
    const { state, window } = await toggle(204, { 'X-Redirect': '/OnboardingWidget' });
    assert.equal(window.location, '/OnboardingWidget');
    assert.equal(state.removed, false);
    assert.equal(state.reads, 0);
});

test('network rejection keeps the row and allows retry', async () => {
    const { state, button } = await toggle(0, {}, true);
    assert.equal(state.removed, false);
    assert.equal(button.disabled, false);
    assert.equal(button.innerHTML, 'Sign up');
    assert.deepEqual(state.toasts, ['Localized network error']);
});
