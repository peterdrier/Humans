const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const view = readFileSync(resolve(__dirname,
    '../../../src/Sections/Humans.Notifications/Views/Notifications/Index.cshtml'), 'utf8');
const script = view.match(/<script>([\s\S]*?)<\/script>/)[1];

test('Enter activates a focused notification link exactly once', () => {
    let clicks = 0;
    let keydown;
    const link = { click() { clicks++; }, closest: () => row };
    const row = { querySelector: () => link };
    const body = { addEventListener(event, handler) { keydown = handler; } };
    vm.runInNewContext(script, { document: {
        activeElement: link,
        getElementById: id => id === 'notificationInboxBody' ? body : null,
        querySelectorAll: () => [],
    } });
    const event = { key: 'Enter', defaultPrevented: false,
        preventDefault() { this.defaultPrevented = true; } };
    keydown(event);
    // A focused anchor's native Enter action also activates it unless cancelled.
    if (!event.defaultPrevented) link.click();
    assert.equal(clicks, 1);
});
