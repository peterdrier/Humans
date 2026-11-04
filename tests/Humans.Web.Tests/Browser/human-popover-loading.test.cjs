const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const source = readFileSync(resolve(__dirname, '../../../src/Humans.Web/wwwroot/js/site.js'), 'utf8');
const script = source.slice(source.indexOf('// Human profile popover'), source.indexOf('// Admin sidebar'));

function page(responses, isPublic = false) {
    const handlers = {};
    let requests = 0;
    const urls = [];
    const popovers = [];
    const element = {
        closest() { return this; },
        getAttribute() { return 'human-id'; },
        hasAttribute: name => name === (isPublic ? 'data-human-popover-public' : 'data-human-popover'),
    };
    const context = {
        document: { addEventListener: (event, handler) => { handlers[event] = handler; } },
        bootstrap: { Popover: class {
            constructor() { this.disposed = false; popovers.push(this); }
            show() {}
            dispose() { this.disposed = true; }
            setContent(content) { this.content = content['.popover-body']; }
        } },
        fetch: async (url, options) => {
            urls.push(url);
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
        element, popovers, urls,
        requests: () => requests,
        hover: async () => {
            handlers.mouseenter({ target: element });
            await new Promise(resolveDone => setImmediate(resolveDone));
        },
        focus: async () => {
            handlers.focusin?.({ target: element });
            await new Promise(resolveDone => setImmediate(resolveDone));
        },
    };
}

for (const isPublic of [false, true]) {
    test(`keyboard focus loads the ${isPublic ? 'public' : 'authenticated'} profile once across focus and hover`, async () => {
        const ui = page([200], isPublic);
        await ui.focus();
        assert.equal(ui.popovers.length, 1);
        assert.equal(ui.popovers[0].content, '<p>Profile</p>');
        assert.deepEqual(ui.urls, [isPublic ? '/Profile/human-id/PublicPopover' : '/Profile/human-id/Popover']);
        await ui.hover();
        await ui.focus();
        assert.equal(ui.requests(), 1);
        assert.equal(ui.popovers.length, 1);
    });
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
