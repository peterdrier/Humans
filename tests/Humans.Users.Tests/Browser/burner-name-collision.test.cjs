const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const view = readFileSync(resolve(__dirname,
    '../../../src/Sections/Humans.Users/Views/Profile/Edit.cshtml'), 'utf8');
const script = view.slice(view.indexOf('// Burner name / other-humans collision warning'),
    view.indexOf('// Scroll to validation summary'));

function page(name = 'Alpha') {
    const handlers = {};
    const input = { value: name, addEventListener: (event, callback) => { handlers[event] = callback; } };
    const classes = new Set(['d-none']);
    const text = { textContent: '' };
    const warning = {
        classList: { add: value => classes.add(value), remove: value => classes.delete(value) },
        querySelector: () => text,
        dataset: { msgOne: '{0} human uses {1}', msgMany: '{0} humans use {1}' },
    };
    const requests = [];
    const timers = new Map();
    let timerId = 0;
    vm.runInNewContext(script, {
        burnerNameInput: input,
        document: { getElementById: () => warning },
        clearTimeout: id => timers.delete(id),
        setTimeout: callback => { const id = ++timerId; timers.set(id, callback); return id; },
        fetch: url => new Promise(resolveResponse => { requests.push({ url, resolveResponse }); }),
    });
    return {
        requests, text,
        hidden: () => classes.has('d-none'),
        type: value => { input.value = value; handlers.input(); },
        debounce: () => { for (const callback of timers.values()) callback(); timers.clear(); },
        respond: async (index, count) => {
            requests[index].resolveResponse({ ok: true, json: async () => ({ count }) });
            await new Promise(resolveDone => setImmediate(resolveDone));
        },
    };
}

test('an old response cannot show during a newer name debounce', async () => {
    const ui = page();
    ui.type('Beta');
    await ui.respond(0, 2);
    assert.equal(ui.hidden(), true);
    ui.debounce();
    await ui.respond(1, 1);
    assert.equal(ui.hidden(), false);
    assert.equal(ui.text.textContent, '1 human uses Beta');
});

test('clearing the name invalidates a pending response without starting another lookup', async () => {
    const ui = page();
    ui.type('A');
    ui.debounce();
    await ui.respond(0, 2);
    assert.equal(ui.requests.length, 1);
    assert.equal(ui.hidden(), true);
});

test('a displayed warning disappears as soon as its name changes', async () => {
    const ui = page();
    await ui.respond(0, 2);
    assert.equal(ui.text.textContent, '2 humans use Alpha');
    assert.equal(ui.hidden(), false);
    ui.type('Beta');
    assert.equal(ui.hidden(), true);
});

test('the newest result remains visible when an older response arrives afterward', async () => {
    const ui = page();
    ui.type('Beta');
    ui.debounce();
    await ui.respond(1, 1);
    await ui.respond(0, 2);
    assert.equal(ui.hidden(), false);
    assert.equal(ui.text.textContent, '1 human uses Beta');
});
