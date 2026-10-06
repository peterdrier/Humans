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

function sortValues(values, sortType = 'auto', nestedValues = []) {
    const rows = values.map(textContent => ({ cells: [{ dataset: {}, textContent }] }));
    const nestedRows = nestedValues.map(textContent => ({ cells: [{ dataset: {}, textContent }] }));
    const nestedBody = { rows: nestedRows };
    const tbody = {
        rows,
        querySelectorAll() { return [...this.rows, ...nestedBody.rows]; },
        appendChild(row) {
            const previousRows = this.rows.includes(row) ? this.rows : nestedBody.rows;
            previousRows.splice(previousRows.indexOf(row), 1);
            this.rows.push(row);
        },
    };
    const attributes = {};
    const header = {
        dataset: { sortCol: '0', sortType }, classList: { add() {}, remove() {} },
        getAttribute(name) { return attributes[name]; },
        setAttribute(name, value) { attributes[name] = value; },
        removeAttribute(name) { delete attributes[name]; },
        querySelector() { return null; },
        addEventListener(event, handler) { this[event] = handler; },
    };
    const table = { tBodies: [tbody], querySelectorAll: () => [header] };
    const context = { document: { querySelectorAll: () => [table] } };
    vm.createContext(context);
    const sortingScript = site.slice(site.indexOf('// Declarative client-side table sorting'),
        site.indexOf('// Declarative client-side table filtering'));
    vm.runInContext(sortingScript, context);
    header.click();
    const ascending = tbody.rows.map(row => row.cells[0].textContent);
    header.click();
    return { ascending, descending: tbody.rows.map(row => row.cells[0].textContent),
        nested: nestedBody.rows.map(row => row.cells[0].textContent) };
}

test('auto sorting compares complete labels rather than their numeric prefixes', () => {
    const labels = ['10 Blue', '2 Blue', '2 Amber', '1 Red'];
    const expected = [...labels].sort((a, b) => a.localeCompare(b));
    const result = sortValues(labels);
    assert.deepEqual(result.ascending, expected);
    assert.deepEqual(result.descending, [...expected].reverse());
});

test('auto and explicit numeric sorting compare complete numeric values numerically', () => {
    for (const sortType of ['auto', 'number']) {
        const result = sortValues(['10.5', '2', '-1', '€1,000.00'], sortType);
        assert.deepEqual(result.ascending, ['-1', '2', '10.5', '€1,000.00']);
        assert.deepEqual(result.descending, ['€1,000.00', '10.5', '2', '-1']);
    }
});


test('mixed text and numeric labels use one text ordering for the whole column', () => {
    const labels = ['2', '10', '1 Red'];
    const expected = [...labels].sort((a, b) => a.localeCompare(b));
    const result = sortValues(labels);
    assert.deepEqual(result.ascending, expected);
    assert.deepEqual(result.descending, [...expected].reverse());
});

function clickRow(overrides = {}, interactive = null) {
    let click;
    const row = { getAttribute: () => '/Governance/BoardVoting/application-1' };
    const context = {
        window: { location: '/Governance/BoardVoting' },
        document: { addEventListener(event, handler) { click = handler; } },
    };
    vm.createContext(context);
    vm.runInContext(site.slice(site.indexOf('// Clickable table rows'),
        site.indexOf('// Declarative client-side table sorting')), context);
    click({ button: 0, target: {
        closest(selector) {
            if (selector === 'tr[data-href]') return row;
            return selector.split(',').some(part => part.trim() === interactive) ? {} : null;
        },
    }, ...overrides });
    return context.window.location;
}

test('ordinary clicks on row content navigate to the row destination', () => {
    assert.equal(clickRow(), '/Governance/BoardVoting/application-1');
});

test('links and controls inside a clickable row retain their own action', () => {
    for (const selector of ['a', 'button', 'input', 'select', 'textarea', 'label', 'summary',
        '[role="button"]', '[role="link"]', '[contenteditable]']) {
        assert.equal(clickRow({}, selector), '/Governance/BoardVoting');
    }
    assert.equal(clickRow({ ctrlKey: true }, 'a'), '/Governance/BoardVoting');
});

test('row navigation ignores handled clicks, modifiers and non-primary buttons', () => {
    for (const overrides of [
        { defaultPrevented: true }, { ctrlKey: true }, { metaKey: true },
        { shiftKey: true }, { altKey: true }, { button: 1 },
    ]) {
        assert.equal(clickRow(overrides), '/Governance/BoardVoting');
    }
});

test('sorting outer rows preserves table rows inside Markdown descriptions', () => {
    const result = sortValues(['Zulu', 'Alpha'], 'auto', ['Item', 'Value', 'A', '1']);
    assert.deepEqual(result.ascending, ['Alpha', 'Zulu']);
    assert.deepEqual(result.descending, ['Zulu', 'Alpha']);
    assert.deepEqual(result.nested, ['Item', 'Value', 'A', '1']);
});
