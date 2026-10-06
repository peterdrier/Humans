const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const source = readFileSync(resolve(__dirname, '../../../src/Humans.Web/wwwroot/js/site.js'), 'utf8');
const script = source.slice(source.indexOf('// Human profile popover'), source.indexOf('// Admin sidebar'));

function page(responses) {
    let hover;
    let requests = 0;
    const popovers = [];
    const element = {
        closest() { return this; },
        getAttribute() { return 'human-id'; },
        hasAttribute: name => name === 'data-human-popover',
    };
    const context = {
        document: { addEventListener: (_, handler) => { hover = handler; } },
        bootstrap: { Popover: class {
            constructor() { this.disposed = false; popovers.push(this); }
            show() {}
            dispose() { this.disposed = true; }
            setContent(content) { this.content = content['.popover-body']; }
        } },
        fetch: async (_, options) => {
            const response = responses[requests++];
            if (response instanceof Error) throw response;
            if (response === 'login redirect') {
                if (options && options.redirect === 'error') throw new TypeError('redirect blocked');
                return { status: 200, ok: true, text: async () => '<form>Login</form>' };
            }
            return { status: response, ok: response === 200, text: async () => '<p>Profile</p>' };
        },
    };
    vm.createContext(context);
    vm.runInContext(script, context);
    return {
        element, popovers,
        requests: () => requests,
        hover: async () => {
            hover({ target: element });
            await new Promise(resolveDone => setImmediate(resolveDone));
        },
    };
}

for (const failure of [503, new Error('offline')]) {
    test(`a failed profile load (${failure}) clears the spinner and permits a later hover`, async () => {
        const ui = page([failure, 200]);
        await ui.hover();
        assert.equal(ui.popovers[0].disposed, true);
        await ui.hover();
        assert.equal(ui.requests(), 2);
        assert.equal(ui.popovers[1].content, '<p>Profile</p>');
    });
}

test('a 404 suppresses the popover without repeatedly probing the absent profile', async () => {
    const ui = page([404]);
    await ui.hover();
    assert.equal(ui.popovers[0].disposed, true);
    await ui.hover();
    assert.equal(ui.requests(), 1);
});


test('a login redirect never becomes cached profile content and a later hover retries', async () => {
    const ui = page(['login redirect', 200]);
    await ui.hover();
    assert.equal(ui.popovers[0].content, undefined);
    assert.equal(ui.popovers[0].disposed, true);
    await ui.hover();
    assert.equal(ui.requests(), 2);
    assert.equal(ui.popovers[1].content, '<p>Profile</p>');
});
