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
