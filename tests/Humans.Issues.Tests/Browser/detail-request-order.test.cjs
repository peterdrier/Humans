const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

function page() {
    const source = readFileSync(resolve(__dirname,
        '../../../src/Sections/Humans.Issues/Views/Issues/Index.cshtml'), 'utf8');
    const section = source.indexOf('@section Scripts');
    const start = source.indexOf('<script>', section) + '<script>'.length;
    const end = source.indexOf("document.querySelectorAll('.issues-filter-form", start);
    assert.ok(section >= 0 && end > start);
    const panel = { innerHTML: '' };
    const requests = [];
    let wired = 0;
    let url = '';
    const context = {
        document: { querySelectorAll: () => [], querySelector: () => null, getElementById: () => panel },
        window: { HumansEmailComposer: { wire: () => { wired++; } } },
        history: { replaceState: (_, __, value) => { url = value; } }, console,
        fetch: () => new Promise(resolveResponse => requests.push(resolveResponse)),
    };
    vm.createContext(context); vm.runInContext(source.slice(start, end), context);
    return {
        panel, wired: () => wired, url: () => url,
        select: id => context.loadIssueDetail(id),
        respond: async (index, html, status = 200) => {
            requests[index]({ ok: status === 200, text: async () => html });
            await new Promise(resolveDone => setImmediate(resolveDone));
        },
    };
}

for (const status of [200, 500]) {
    test(`a stale ${status} response cannot replace the selected detail or wire its forms`, async () => {
        const ui = page(); ui.select('A'); ui.select('B');
        await ui.respond(1, '<form>Current B</form>');
        await ui.respond(0, '<form>Old A</form>', status);
        assert.equal(ui.panel.innerHTML, '<form>Current B</form>');
        assert.equal(ui.wired(), 1);
        assert.equal(ui.url(), '/Issues?selected=B');
    });
}

test('an earlier detail cannot appear while the latest selection is pending', async () => {
    const ui = page(); ui.select('A'); ui.select('B');
    await ui.respond(0, '<form>Old A</form>');
    assert.equal(ui.panel.innerHTML, '');
    assert.equal(ui.wired(), 0);
    await ui.respond(1, '<form>Current B</form>');
    assert.equal(ui.panel.innerHTML, '<form>Current B</form>');
    assert.equal(ui.wired(), 1);
});
