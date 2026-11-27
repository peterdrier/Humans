const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const source = readFileSync(resolve(__dirname,
    '../../../src/Sections/Humans.Shifts/Views/Shifts/_DepartmentCoveragePieRow.cshtml'), 'utf8');
const script = source.match(/<script>([\s\S]*?)<\/script>/)[1];

function loadTip({ stored = null, readThrows = false, writeThrows = false, accessThrows = false } = {}) {
    let hidden = true, click;
    const tip = {
        removeAttribute: name => { assert.equal(name, 'hidden'); hidden = false; },
        setAttribute: name => { assert.equal(name, 'hidden'); hidden = true; },
        querySelector: selector => {
            assert.equal(selector, '[data-dept-pie-tip-dismiss]');
            return { addEventListener: (name, handler) => { assert.equal(name, 'click'); click = handler; } };
        },
    };
    const storage = {
        getItem: key => {
            assert.equal(key, 'humans.pieTipDismissed');
            if (readThrows) throw new DOMException('Storage blocked', 'SecurityError');
            return stored;
        },
        setItem: (key, value) => {
            assert.equal(key, 'humans.pieTipDismissed');
            if (writeThrows) throw new DOMException('Storage full', 'QuotaExceededError');
            stored = value;
        },
    };
    const context = { document: { querySelector: selector => {
        assert.equal(selector, '[data-dept-pie-tip]');
        return tip;
    } } };
    Object.defineProperty(context, 'localStorage', { get: () => {
        if (accessThrows) throw new DOMException('Storage blocked', 'SecurityError');
        return storage;
    } });
    vm.runInNewContext(script, context);
    return { hidden: () => hidden, stored: () => stored, dismiss: () => click() };
}

test('coverage hint is shown until dismissal is remembered', () => {
    const ui = loadTip();
    assert.equal(ui.hidden(), false);
    ui.dismiss();
    assert.equal(ui.hidden(), true);
    assert.equal(ui.stored(), '1');
});

test('remembered coverage hint stays hidden', () => {
    assert.equal(loadTip({ stored: '1' }).hidden(), true);
});

for (const failure of ['readThrows', 'accessThrows', 'writeThrows']) {
    test(`coverage hint remains dismissible when ${failure}`, () => {
        const ui = loadTip({ [failure]: true });
        assert.equal(ui.hidden(), false);
        ui.dismiss();
        assert.equal(ui.hidden(), true);
    });
}
