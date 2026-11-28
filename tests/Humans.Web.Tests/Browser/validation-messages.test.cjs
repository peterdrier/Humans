const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const site = readFileSync(resolve(__dirname, '../../../src/Humans.Web/wwwroot/js/site.js'), 'utf8');
const script = site.slice(site.indexOf('// Ties each validation message'), site.indexOf('// Favourite hearts'));

function element(attributes, classes = []) {
    return {
        id: attributes.id || '',
        classList: { contains: name => classes.includes(name) },
        getAttribute(name) { return attributes[name] ?? null; },
        setAttribute(name, value) { attributes[name] = value; },
    };
}

test('a server-rejected field is described by its message and marked invalid', () => {
    const fieldAttributes = { id: 'BurnerName', name: 'BurnerName', 'aria-describedby': 'BurnerNameHelp' };
    const field = element(fieldAttributes, ['input-validation-error']);
    const message = { id: '', dataset: { valmsgFor: 'BurnerName' }, closest: () => ({ querySelectorAll: () => [field] }) };
    const context = { document: { querySelectorAll: () => [message] } };
    vm.createContext(context);
    vm.runInContext(script, context);

    assert.equal(message.id, 'BurnerName-valmsg');
    assert.equal(fieldAttributes['aria-describedby'], 'BurnerNameHelp BurnerName-valmsg');
    assert.equal(fieldAttributes['aria-invalid'], 'true');
});
