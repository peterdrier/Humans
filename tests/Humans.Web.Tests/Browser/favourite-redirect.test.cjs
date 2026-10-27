const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const source = readFileSync(resolve(__dirname, '../../../src/Humans.Web/wwwroot/js/site.js'), 'utf8');
const script = source.slice(source.indexOf('// Favourite hearts'));

function page(removing, redirected) {
    let click, removed = 0, reloads = 0;
    const errors = [];
    const attrs = new Map(Object.entries({
        'data-event-id': 'event-id',
        'data-favourited': String(removing),
        'data-remove-row': String(removing),
        'data-title-add': 'Add',
        'data-title-remove': 'Remove',
        'data-error': 'Save failed',
    }));
    const row = { remove: () => { removed++; }, closest: () => null };
    const button = {
        disabled: false,
        getAttribute: name => attrs.get(name) ?? null,
        setAttribute: (name, value) => attrs.set(name, value),
        classList: { toggle() {} },
        closest: selector => selector === 'form'
            ? { querySelector: () => ({ value: 'token' }) }
            : selector === '.list-group-item' ? row : button,
    };
    vm.runInNewContext(script, {
        document: {
            addEventListener: (_, handler) => { click = handler; },
            querySelectorAll: () => [button],
            querySelector: () => removed ? null : button,
        },
        window: { location: { reload: () => { reloads++; } } },
        showToast: message => errors.push(message),
        fetch: async (_, options) => {
            assert.equal(options.method, removing ? 'DELETE' : 'POST');
            if (redirected && options.redirect === 'error') throw new TypeError('redirect blocked');
            return { ok: true, status: 200 };
        },
    });
    return {
        button, attrs, errors,
        removed: () => removed,
        reloads: () => reloads,
        click: async () => {
            click({ target: button });
            await new Promise(done => setImmediate(done));
        },
    };
}

for (const removing of [false, true]) {
    test(`a redirected ${removing ? 'schedule removal' : 'favourite add'} preserves the displayed state`, async () => {
        const ui = page(removing, true);
        await ui.click();
        assert.equal(ui.attrs.get('data-favourited'), String(removing));
        assert.equal(ui.removed(), 0);
        assert.equal(ui.reloads(), 0);
        assert.deepEqual(ui.errors, ['Save failed']);
        assert.equal(ui.button.disabled, false);
    });

    test(`a successful ${removing ? 'schedule removal' : 'favourite add'} updates the displayed state`, async () => {
        const ui = page(removing, false);
        await ui.click();
        if (removing) {
            assert.equal(ui.removed(), 1);
            assert.equal(ui.reloads(), 1);
        } else {
            assert.equal(ui.attrs.get('data-favourited'), 'true');
            assert.equal(ui.attrs.get('aria-pressed'), 'true');
        }
        assert.deepEqual(ui.errors, []);
        assert.equal(ui.button.disabled, false);
    });
}
