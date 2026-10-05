const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

function keypad() {
    const completed = [];
    const filled = [false, false, false, false];
    const timers = new Map();
    let timerId = 0, click;
    const dots = filled.map((_, index) => ({ classList: { toggle: (_, value) => { filled[index] = value; } } }));
    const container = { querySelectorAll: () => dots, querySelector: () => ({ addEventListener: (_, callback) => { click = callback; } }) };
    const context = { window: {},
        setTimeout: callback => { const id = ++timerId; timers.set(id, callback); return id; },
        clearTimeout: id => timers.delete(id) };
    vm.createContext(context);
    vm.runInContext(readFileSync(resolve(__dirname,
        '../../../src/Sections/Humans.Gate/wwwroot/js/gate/gate-keypad.js'), 'utf8'), context);
    const pad = context.window.initGateKeypad(container, pin => completed.push(pin));
    return { completed, filled, reset: pad.reset,
        enter: pin => { for (const digit of pin) click({ target: { closest: () => ({ dataset: { digit } }) } }); },
        paint: () => { for (const callback of timers.values()) callback(); timers.clear(); } };
}

test('reset cancels a completed PIN before its delayed submission', () => {
    const p = keypad(); p.enter('1234'); p.reset(); p.paint();
    assert.deepEqual(p.completed, []);
    assert.deepEqual(p.filled, [false, false, false, false]);
});

test('resetting and entering another PIN submits only the new entry', () => {
    const p = keypad(); p.enter('1234'); p.reset(); p.enter('5678'); p.paint();
    assert.deepEqual(p.completed, ['5678']);
});

test('a normal complete PIN submits once after painting and can be reset', () => {
    const p = keypad(); p.enter('123456');
    assert.deepEqual(p.completed, []);
    assert.deepEqual(p.filled, [true, true, true, true]);
    p.paint(); assert.deepEqual(p.completed, ['1234']);
    p.reset(); p.enter('5678'); p.paint(); assert.deepEqual(p.completed, ['1234', '5678']);
});

function verdictRenderer(redirected) {
    const source = readFileSync(resolve(__dirname,
        '../../../src/Sections/Humans.Gate/wwwroot/js/gate/gate.js'), 'utf8');
    const script = source.slice(source.indexOf('    async function render('),
        source.indexOf('    // Never leave the operator'));
    const result = { innerHTML: 'previous card' };
    const requests = [];
    const context = {
        result, token: 'antiforgery', clearResetTimer() {}, afterRender() {},
        renderError: () => { result.innerHTML = 'Request failed'; },
        console: { error() {} },
        fetch: async (url, options) => {
            requests.push({ url, options });
            if (redirected && options.redirect === 'error') throw new TypeError('redirect blocked');
            return { ok: true, text: async () => redirected ? '<form>Login</form>' : 'verdict card' };
        },
    };
    vm.createContext(context);
    vm.runInContext(script + '\nthis.renderVerdict = render;', context);
    return { result, requests, render: context.renderVerdict };
}

for (const body of [null, 'barcode=TICKET']) {
    test(`verdict ${body === null ? 'lookup' : 'decision'} rejects a redirected login page`, async () => {
        const ui = verdictRenderer(true);
        await ui.render('/Gate/Card', body);
        assert.equal(ui.result.innerHTML, 'Request failed');
    });
    test(`verdict ${body === null ? 'lookup' : 'decision'} still renders a successful partial`, async () => {
        const ui = verdictRenderer(false);
        await ui.render('/Gate/Card', body);
        assert.equal(ui.result.innerHTML, 'verdict card');
        assert.equal(ui.requests[0].options.method, body === null ? undefined : 'POST');
        if (body !== null) {
            assert.equal(ui.requests[0].options.headers.RequestVerificationToken, 'antiforgery');
            assert.equal(ui.requests[0].options.body, body);
        }
    });
}
