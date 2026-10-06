const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

function page() {
    const source = readFileSync(resolve(__dirname,
        '../../../src/Sections/Humans.Shifts/Views/ShiftAdmin/Index.cshtml'), 'utf8');
    const start = source.indexOf('// Range voluntell:');
    const end = source.indexOf('// Scroll to fragment', start);
    assert.ok(start >= 0 && end > start);
    const script = source.slice(start, end).replace('@Html.Raw(searchUrl)', '/search');
    const handlers = {};
    const timers = new Map();
    let timerId = 0;
    const rotas = ['one', 'two'].map(id => {
        const result = { innerHTML: '' };
        const hidden = { value: 'Previously selected' };
        const button = { disabled: false };
        const input = { value: '', dataset: { rotaId: id, shiftId: id },
            classList: { contains: name => name === 'voluntell-range-search' } };
        const form = { querySelector: selector => ({
            '.voluntell-range-userId': hidden, '.voluntell-range-search': input,
            '.voluntell-range-results': result, '.voluntell-range-submit': button,
            'button[type="submit"]': button,
        })[selector] };
        input.closest = () => form;
        return { id, input, result, hidden, button, form };
    });
    const requests = [];
    const context = {
        document: {
            addEventListener: (name, handler) => { handlers[name] = handler; },
            querySelector: selector => rotas.find(r => selector === `.voluntell-range-results[data-rota-id="${r.id}"]`)?.result ?? null,
        },
        escapeHtml: value => String(value),
        setTimeout: callback => { timers.set(++timerId, callback); return timerId; },
        clearTimeout: id => timers.delete(id),
        fetch: url => new Promise((resolveResponse, reject) => requests.push({ url, resolveResponse, reject })),
    };
    vm.createContext(context); vm.runInContext(script, context);
    return {
        rotas, requests,
        type: (index, text) => { rotas[index].input.value = text; handlers.input({ target: rotas[index].input }); },
        debounce: () => { for (const callback of timers.values()) callback(); timers.clear(); },
        respond: async (index, name, fail = false) => {
            if (fail) requests[index].reject(new Error('offline'));
            else requests[index].resolveResponse({ json: async () => [{ userId: name, displayName: name, bookedShiftCount: 0 }] });
            await new Promise(resolveDone => setImmediate(resolveDone));
        },
        pick: (index, name) => {
            const pick = { dataset: { rotaId: rotas[index].id, userId: name, displayName: name }, closest: () => rotas[index].form };
            handlers.click({ target: { closest: () => pick } });
        },
    };
}

test('editing a range search clears the selected identity and disables assignment', () => {
    const ui = page(); ui.type(0, 'Alpha');
    assert.equal(ui.rotas[0].hidden.value, '');
    assert.equal(ui.rotas[0].button.disabled, true);
});

test('editing removes old selectable rows immediately', async () => {
    const ui = page(); ui.type(0, 'Alpha'); ui.debounce(); await ui.respond(0, 'Alpha');
    ui.type(0, 'Beta');
    assert.equal(ui.rotas[0].result.innerHTML, '');
});

for (const fail of [false, true]) {
    test(`an old ${fail ? 'error' : 'result'} cannot override a newer range search`, async () => {
        const ui = page(); ui.type(0, 'Alpha'); ui.debounce(); ui.type(0, 'Beta'); ui.debounce();
        await ui.respond(1, 'Beta'); await ui.respond(0, 'Alpha', fail);
        assert.match(ui.rotas[0].result.innerHTML, /Beta/);
        assert.doesNotMatch(ui.rotas[0].result.innerHTML, /Alpha|Search failed/);
    });
}

test('clearing the query invalidates a pending result before debounce', async () => {
    const ui = page(); ui.type(0, 'Alpha'); ui.debounce(); ui.type(0, '');
    await ui.respond(0, 'Alpha');
    assert.equal(ui.rotas[0].result.innerHTML, '');
});

test('range searches for different rotas debounce independently', async () => {
    const ui = page(); ui.type(0, 'Alpha'); ui.type(1, 'Beta'); ui.debounce();
    assert.equal(ui.requests.length, 2);
    await ui.respond(0, 'Alpha'); await ui.respond(1, 'Beta');
    assert.match(ui.rotas[0].result.innerHTML, /Alpha/);
    assert.match(ui.rotas[1].result.innerHTML, /Beta/);
});

test('picking a current result keeps the existing assignment behavior', async () => {
    const ui = page(); ui.type(0, 'Alpha'); ui.debounce(); await ui.respond(0, 'Alpha'); ui.pick(0, 'Alpha');
    assert.equal(ui.rotas[0].hidden.value, 'Alpha');
    assert.equal(ui.rotas[0].input.value, 'Alpha');
    assert.equal(ui.rotas[0].button.disabled, false);
    assert.equal(ui.rotas[0].result.innerHTML, '');
});
