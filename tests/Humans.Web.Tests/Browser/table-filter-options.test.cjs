const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const site = readFileSync(resolve(__dirname, '../../../src/Humans.Web/wwwroot/js/site.js'), 'utf8');
const script = site.slice(site.indexOf('// Declarative client-side table filtering'), site.indexOf('// Timezone detection'));

test('dropdown filters retain labels that match object properties and filter their rows', () => {
    const labels = ['Operations', 'constructor', 'constructor', 'toString', '__proto__', '', '—'];
    const rows = labels.map(text => ({ cells: [{ textContent: text }], textContent: text, style: {} }));
    const input = {
        tagName: 'SELECT', dataset: { filterCol: '0' }, options: [{ value: '', textContent: 'All' }],
        value: '',
        appendChild(option) { this.options.push(option); },
        addEventListener(event, handler) { this[event] = handler; },
    };
    const table = { tBodies: [{ rows }] };
    const root = {
        querySelector(selector) { return selector === 'table' ? table : null; },
        querySelectorAll() { return [input]; },
    };
    const context = {
        document: { querySelectorAll: () => [root], createElement: () => ({}) },
    };
    vm.createContext(context);
    vm.runInContext(script, context);

    assert.deepEqual(input.options.map(option => option.value),
        ['', 'Operations', 'constructor', 'toString', '__proto__']);
    input.value = 'constructor';
    input.change();
    assert.deepEqual(rows.filter(row => row.style.display !== 'none').map(row => row.textContent),
        ['constructor', 'constructor']);
});
