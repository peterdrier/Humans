const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

function page() {
    const source = readFileSync(resolve(__dirname,
        '../../../src/Humans.Base/Views/Shared/_VolunteerSearchScript.cshtml'), 'utf8');
    let script = source.slice(source.indexOf('(function () {'));
    const serialized = {
        searchUrl: '/search', voluntellUrl: '/assign', assignButtonClass: 'btn-success',
        dietaryPreferenceStyle: 'muted', noResultsText: 'No humans found.',
        errorText: 'Search failed.', overlapLabel: 'Overlap',
    };
    script = script.replace(/@Html\.Raw\(System\.Text\.Json\.JsonSerializer\.Serialize\((\w+)\)\)/g,
        (_, name) => JSON.stringify(serialized[name]));
    for (const [key, value] of Object.entries({
        '@Html.Raw(Html.AntiForgeryTokenHtmlForJavaScript())': '',
        '@showPoolBadge.ToString().ToLowerInvariant()': 'false',
    })) script = script.replaceAll(key, value);
    const handlers = {};
    const timers = new Map();
    const requests = [];
    let timerId = 0;
    const inputs = ['one', 'two'].map(shiftId => ({
        value: '', dataset: { shiftId }, classList: { contains: () => true },
    }));
    const containers = { one: { innerHTML: '' }, two: { innerHTML: '' } };
    vm.runInNewContext(script, {
        document: {
            addEventListener: (event, handler) => { handlers[event] = handler; },
            querySelector: selector => containers[selector.includes('"one"') ? 'one' : 'two'],
        },
        escapeHtml: value => String(value ?? ''),
        setTimeout: callback => { const id = ++timerId; timers.set(id, callback); return id; },
        clearTimeout: id => timers.delete(id),
        fetch: url => new Promise((resolveResponse, reject) => requests.push({ url, resolveResponse, reject })),
    });
    return {
        requests, containers,
        type: (index, name) => { inputs[index].value = name; handlers.input({ target: inputs[index] }); },
        debounce: () => { for (const callback of timers.values()) callback(); timers.clear(); },
        respond: async (index, name, fail = false) => {
            if (fail) requests[index].reject(new Error('network failure'));
            else requests[index].resolveResponse({ json: async () => [{ displayName: name, userId: name }] });
            await new Promise(resolveDone => setImmediate(resolveDone));
        },
    };
}

test('slower older results cannot replace the latest volunteer query', async () => {
    const ui = page(); ui.type(0, 'Alpha'); ui.debounce(); ui.type(0, 'Beta'); ui.debounce();
    await ui.respond(1, 'Beta');
    await ui.respond(0, 'Alpha');
    assert.match(ui.containers.one.innerHTML, /Beta/);
    assert.doesNotMatch(ui.containers.one.innerHTML, /Alpha/);
});

test('clearing a query prevents pending results from returning', async () => {
    const ui = page(); ui.type(0, 'Alpha'); ui.debounce(); ui.type(0, ''); ui.debounce();
    await ui.respond(0, 'Alpha');
    assert.equal(ui.containers.one.innerHTML, '');
});

test('an old error cannot overwrite newer successful results', async () => {
    const ui = page(); ui.type(0, 'Alpha'); ui.debounce(); ui.type(0, 'Beta'); ui.debounce();
    await ui.respond(1, 'Beta');
    await ui.respond(0, '', true);
    assert.match(ui.containers.one.innerHTML, /Beta/);
    assert.doesNotMatch(ui.containers.one.innerHTML, /Search failed/);
});

test('different shift inputs debounce independently', async () => {
    const ui = page(); ui.type(0, 'Alpha'); ui.type(1, 'Beta'); ui.debounce();
    assert.equal(ui.requests.length, 2);
    await ui.respond(0, 'Alpha'); await ui.respond(1, 'Beta');
    assert.match(ui.containers.one.innerHTML, /Alpha/);
    assert.match(ui.containers.two.innerHTML, /Beta/);
});
