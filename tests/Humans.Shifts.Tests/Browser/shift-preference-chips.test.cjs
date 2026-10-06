const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const view = fs.readFileSync(path.resolve(__dirname,
    '../../../src/Sections/Humans.Shifts/Views/ShiftProfile/ShiftInfo.cshtml'), 'utf8');
const script = view.slice(view.indexOf('    // Chip toggle'), view.indexOf('    // Radio card toggle'));

function chip(value, text) {
    const handlers = {};
    const checkbox = { value, checked: false };
    const field = { style: { display: 'none' } };
    const selected = new Set();
    const attributes = {};
    const element = {
        textContent: text,
        querySelector: () => checkbox,
        closest: () => ({ querySelector: () => field }),
        classList: { toggle(name, enabled) { enabled ? selected.add(name) : selected.delete(name); } },
        setAttribute: (name, val) => attributes[name] = val,
        addEventListener: (name, handler) => handlers[name] = handler,
    };
    vm.runInNewContext(script, { document: { querySelectorAll: () => [element] } });
    return { checkbox, field, selected, attributes,
        trigger(name, key) { handlers[name]({ key, preventDefault() {} }); } };
}

for (const label of ['Other', 'Otro', 'Sonstiges', 'Altro', 'Autre', 'Altres']) {
    test(`Other free text follows selection with label ${label}`, () => {
        const item = chip('Other', `🌐 ${label}`);
        item.trigger('click');
        assert.equal(item.field.style.display, 'block');
        assert.equal(item.checkbox.checked, true);
        assert.equal(item.attributes['aria-checked'], 'true');
        assert.equal(item.selected.has('selected'), true);
        item.trigger('keydown', ' ');
        assert.equal(item.field.style.display, 'none');
        assert.equal(item.checkbox.checked, false);
        assert.equal(item.attributes['aria-checked'], 'false');
    });
}

test('a regular option cannot change the Other free-text field', () => {
    const item = chip('Driving', 'Other');
    item.trigger('keydown', 'Enter');
    assert.equal(item.checkbox.checked, true);
    assert.equal(item.field.style.display, 'none');
});
