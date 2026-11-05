const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const site = readFileSync(resolve(__dirname, '../../../src/Humans.Web/wwwroot/js/site.js'), 'utf8');
const script = site.slice(site.indexOf('// Copy to clipboard:'), site.indexOf('// Human profile popover'));

function setup({ source, clipboard = true, message }) {
    let click;
    const written = [];
    const toasts = [];
    const trigger = { getAttribute: name => ({ 'data-copy': '#src', 'data-copied': message })[name] };
    const context = {
        document: {
            addEventListener: (type, handler) => { if (type === 'click') click = handler; },
            querySelector: selector => (selector === '#src' ? source : null),
        },
        navigator: clipboard ? { clipboard: { writeText: text => { written.push(text); return Promise.resolve(); } } } : {},
        showToast: text => toasts.push(text),
    };
    vm.createContext(context);
    vm.runInContext(script, context);
    const fire = target => click({ target: { closest: () => target } });
    return { fire, trigger, written, toasts };
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

test('ignores clicks outside a data-copy trigger, missing sources and missing clipboard', () => {
    const outside = setup({ source: { tagName: 'INPUT', value: 'x' } });
    outside.fire(null);
    const missing = setup({ source: null });
    missing.fire(missing.trigger);
    const noClipboard = setup({ source: { tagName: 'INPUT', value: 'x' }, clipboard: false });
    noClipboard.fire(noClipboard.trigger);
    assert.deepEqual([outside.written, missing.written, noClipboard.written], [[], [], []]);
});
