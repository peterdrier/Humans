const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const site = readFileSync(resolve(__dirname, '../../../src/Humans.Web/wwwroot/js/site.js'), 'utf8');
const script = site.slice(site.indexOf('// Copy to clipboard:'), site.indexOf('// Human profile popover'));

function setup({ source, clipboard = true, message, failure }) {
    let click;
    const written = [];
    const toasts = [], types = [], warnings = [];
    const trigger = { getAttribute: name => ({ 'data-copy': '#src', 'data-copied': message })[name] };
    const context = {
        document: {
            addEventListener: (type, handler) => { if (type === 'click') click = handler; },
            getElementById: id => id === 'toastContainer' ? { dataset: { copyError: 'Localized copy failure' } } : null,
            querySelector: selector => (selector === '#src' ? source : null),
        },
        navigator: clipboard ? { clipboard: failure === 'missing-method' ? {} : { writeText: text => {
            written.push(text);
            if (failure === 'throws') throw new Error('denied');
            return failure === 'rejected' ? Promise.reject(new Error('denied')) : Promise.resolve();
        } } } : {},
        showToast: (text, type) => { toasts.push(text); types.push(type); },
        console: { warn: (...args) => warnings.push(args) },
    };
    vm.createContext(context);
    vm.runInContext(script, context);
    const fire = target => click({ target: { closest: () => target } });
    return { fire, trigger, written, toasts, types, warnings };
}

test('copies an input by value and toasts when data-copied is set', async () => {
    const { fire, trigger, written, toasts } = setup({ source: { tagName: 'INPUT', value: 'abc' }, message: 'Copied' });
    fire(trigger);
    await Promise.resolve();
    assert.deepEqual(written, ['abc']);
    assert.deepEqual(toasts, ['Copied']);
});

test('copies non-field elements by textContent and stays silent without data-copied', async () => {
    const { fire, trigger, written, toasts } = setup({ source: { tagName: 'PRE', textContent: 'secret' } });
    fire(trigger);
    await Promise.resolve();
    assert.deepEqual(written, ['secret']);
    assert.deepEqual(toasts, []);
});

test('never writes without a trigger, source or clipboard', () => {
    const outside = setup({ source: { tagName: 'INPUT', value: 'x' } });
    outside.fire(null);
    const missing = setup({ source: null });
    missing.fire(missing.trigger);
    const noClipboard = setup({ source: { tagName: 'INPUT', value: 'x' }, clipboard: false });
    noClipboard.fire(noClipboard.trigger);
    assert.deepEqual([outside.written, missing.written, noClipboard.written], [[], [], []]);
});

for (const failure of ['rejected', 'throws', 'unsupported', 'missing-method']) {
    test(`clipboard ${failure} reports failure without a copied notification`, async () => {
        const ui = setup({ source: { tagName: 'PRE', textContent: 'secret' }, message: 'Copied',
            clipboard: failure !== 'unsupported', failure });
        assert.doesNotThrow(() => ui.fire(ui.trigger));
        await new Promise(resolve => setImmediate(resolve));
        assert.deepEqual(ui.toasts, ['Localized copy failure']);
        assert.deepEqual(ui.types, ['danger']);
        assert.equal(ui.warnings.length, 1);
        assert.ok(!JSON.stringify(ui.warnings).includes('secret'));
        if (failure === 'unsupported' || failure === 'missing-method') assert.deepEqual(ui.written, []);
    });
}
