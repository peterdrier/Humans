const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

function page(initial = '') {
    const source = readFileSync(resolve(__dirname,
        '../../../src/Sections/Humans.Teams/Views/Shared/_TeamGoogleAndParentFields.cshtml'), 'utf8');
    const start = source.indexOf('(function () {');
    const end = source.indexOf('</script>', start);
    assert.ok(start >= 0 && end > start);
    const classes = new Set(['d-none']);
    const warning = { classList: { add: name => classes.add(name), remove: name => classes.delete(name) } };
    let change;
    const select = { value: initial, addEventListener: (_, handler) => { change = handler; } };
    const resources = { innerHTML: '' };
    const elements = { parentTeamSelect: select, parentResourceWarning: warning, parentResourceList: resources };
    const requests = [];
    const context = {
        document: {
            getElementById: id => elements[id],
            createTextNode: text => ({ textContent: text }),
            createElement: () => ({ appendChild(child) { this.innerHTML = child.textContent; } }),
        },
        fetch: url => new Promise((resolveResponse, reject) => requests.push({ url, resolveResponse, reject })),
    };
    vm.createContext(context); vm.runInContext(source.slice(start, end), context);
    return {
        resources, hidden: () => classes.has('d-none'),
        select: value => { select.value = value; change(); },
        respond: async (index, name, fail = false) => {
            if (fail) requests[index].reject(new Error('offline'));
            else requests[index].resolveResponse({ ok: true, json: async () => [{ name, type: 'Drive Folder' }] });
            await new Promise(resolveDone => setImmediate(resolveDone));
        },
    };
}

for (const fail of [false, true]) {
    test(`an earlier parent ${fail ? 'error' : 'response'} cannot change the current inheritance warning`, async () => {
        const ui = page(); ui.select('A'); ui.select('B');
        await ui.respond(1, 'B folder'); await ui.respond(0, 'A folder', fail);
        assert.equal(ui.hidden(), false);
        assert.match(ui.resources.innerHTML, /B folder/);
        assert.doesNotMatch(ui.resources.innerHTML, /A folder/);
    });
}

test('clearing the parent invalidates its pending resource lookup', async () => {
    const ui = page(); ui.select('A'); ui.select(''); await ui.respond(0, 'A folder');
    assert.equal(ui.hidden(), true);
    assert.equal(ui.resources.innerHTML, '');
});

test('changing the parent immediately removes the previous inheritance warning', async () => {
    const ui = page(); ui.select('A'); await ui.respond(0, 'A folder'); ui.select('B');
    assert.equal(ui.hidden(), true);
    assert.equal(ui.resources.innerHTML, '');
    await ui.respond(1, 'B folder');
    assert.equal(ui.hidden(), false);
    assert.match(ui.resources.innerHTML, /B folder/);
});

test('an initially selected parent still shows its current resources', async () => {
    const ui = page('A'); await ui.respond(0, 'A folder');
    assert.equal(ui.hidden(), false);
    assert.match(ui.resources.innerHTML, /A folder/);
});
