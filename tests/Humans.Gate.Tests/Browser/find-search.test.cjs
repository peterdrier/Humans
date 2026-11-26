const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const source = readFileSync(resolve(__dirname,
    '../../../src/Sections/Humans.Gate/wwwroot/js/gate/gate.js'), 'utf8');
const start = source.indexOf('    function initFind()');
const end = source.indexOf('    function initOverride()', start);
assert.ok(start >= 0 && end > start);
const script = source.slice(start, end) + '\ninitFind();';
const flush = () => new Promise(setImmediate);

function finder() {
    const handlers = {}, timers = new Map(), requests = [], errors = [];
    let timerId = 0;
    const results = { innerHTML: '', replaceChildren() { this.innerHTML = ''; }, addEventListener() {} };
    const input = { value: '', focus() {}, addEventListener: (name, handler) => handlers[name] = handler };
    const elements = {
        '#gate-find-input': input,
        '#gate-find-form': { addEventListener() {} },
        '#gate-find-results': results,
        '[data-find-cancel]': { addEventListener() {} },
    };
    const context = {
        find: { querySelector: selector => elements[selector], classList: { add() {}, remove() {} },
            setAttribute() {}, addEventListener() {} },
        document: { getElementById: () => ({ addEventListener: (_, handler) => handlers.open = handler }) },
        readyCard: null, result: { replaceChildren() {} }, clearResetTimer() {}, focusInput() {},
        findUrl: '/Gate/Find', evaluate() {},
        console: { error: (...args) => errors.push(args) },
        setTimeout: (callback, delay) => { timers.set(++timerId, { callback, delay }); return timerId; },
        clearTimeout: id => timers.delete(id),
        fetch: url => new Promise((complete, fail) => requests.push({ url, complete, fail })),
    };
    const api = vm.runInNewContext(script, context);
    handlers.open();
    return {
        results, requests, errors, close: api.close,
        type: text => { input.value = text; handlers.input(); },
        debounce: () => {
            for (const [id, timer] of timers) {
                if (timer.delay !== 400) continue;
                timers.delete(id);
                timer.callback();
            }
        },
        reply: async (index, html) => { requests[index].complete({ ok: true, text: async () => html }); await flush(); },
    };
}

test('editing a name immediately removes the previous choices', async () => {
    const ui = finder();
    ui.type('Alice'); ui.debounce(); await ui.reply(0, 'Alice matches');
    ui.type('Bob');
    assert.equal(ui.results.innerHTML, '');
    assert.equal(ui.requests.length, 1);
});

test('an old response cannot render during the next query debounce', async () => {
    const ui = finder();
    ui.type('Alice'); ui.debounce(); ui.type('Bob');
    await ui.reply(0, 'Alice matches');
    assert.equal(ui.results.innerHTML, '');
});

test('editing while an old response body loads invalidates its choices', async () => {
    const ui = finder();
    ui.type('Alice'); ui.debounce();
    let finishBody;
    ui.requests[0].complete({ ok: true, text: () => new Promise(done => { finishBody = done; }) });
    await flush();
    ui.type('Bob'); finishBody('Alice matches'); await flush();
    assert.equal(ui.results.innerHTML, '');
});

test('an old error cannot replace choices during the next debounce', async () => {
    const ui = finder();
    ui.type('Alice'); ui.debounce(); ui.type('Bob');
    ui.requests[0].fail(new Error('offline')); await flush();
    assert.equal(ui.results.innerHTML, '');
    assert.equal(ui.errors.length, 0);
});

test('the newest query still renders its matches', async () => {
    const ui = finder();
    ui.type('Alice'); ui.debounce(); ui.type('Bob'); ui.debounce();
    assert.equal(ui.requests[1].url, '/Gate/Find?q=Bob');
    await ui.reply(1, 'Bob matches'); await ui.reply(0, 'Alice matches');
    assert.equal(ui.results.innerHTML, 'Bob matches');
});

test('closing invalidates pending responses and the next search timer', async () => {
    const ui = finder();
    ui.type('Alice'); ui.debounce(); ui.type('Bob'); ui.close(); ui.debounce();
    await ui.reply(0, 'Alice matches');
    assert.equal(ui.results.innerHTML, '');
    assert.equal(ui.requests.length, 1);
});

test('a current search failure still shows connection feedback', async () => {
    const ui = finder();
    ui.type('Alice'); ui.debounce();
    ui.requests[0].fail(new Error('offline')); await flush();
    assert.match(ui.results.innerHTML, /Connection problem/);
    assert.equal(ui.errors.length, 1);
});
