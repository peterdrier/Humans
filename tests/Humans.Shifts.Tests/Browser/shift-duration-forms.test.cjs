const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const source = readFileSync(resolve(__dirname, '../../../src/Sections/Humans.Shifts/Views/ShiftAdmin/Index.cshtml'), 'utf8');
const start = source.indexOf('    function reindexSlots(container)');
const script = source.slice(start, source.indexOf('    // A min above the cap', start));
const initialStart = source.indexOf('<label class="form-label form-label-sm">Time Slots</label>');
const initialMarkup = source.slice(initialStart, source.indexOf('class="btn btn-sm btn-outline-secondary add-slot-btn"', initialStart));

function slots() {
    const rows = [];
    let add;
    const container = {
        querySelectorAll: () => rows,
        appendChild: row => rows.push(row),
    };
    function row() {
        let inputs = [], remove;
        return {
            set innerHTML(html) {
                inputs = [...html.matchAll(/<input\b[^>]*>/g)].map(tag => {
                    const attrs = Object.fromEntries([...tag[0].matchAll(/([\w-]+)="([^"]*)"/g)].map(a => [a[1], a[2]]));
                    return { name: attrs.name, value: attrs.value };
                });
            },
            querySelectorAll: () => inputs,
            querySelector: () => ({ addEventListener: (_, handler) => { remove = handler; } }),
            remove() { rows.splice(rows.indexOf(this), 1); },
            clickRemove() { remove.call({ closest: () => this }); },
        };
    }
    const initial = row();
    initial.innerHTML = initialMarkup;
    rows.push(initial);
    const button = { dataset: { rotaId: 'rota' }, addEventListener: (_, handler) => { add = handler; } };
    vm.runInNewContext(script, {
        document: {
            querySelectorAll: () => [button],
            getElementById: () => container,
            createElement: row,
        },
    });
    return { rows, add: () => add.call(button) };
}

function assertFields(rows) {
    rows.forEach((row, index) => {
        const inputs = row.querySelectorAll('input[name]');
        const duration = inputs.find(i => i.name === `TimeSlots[${index}].DurationHours`);
        assert.ok(duration, `slot ${index} must post its own duration`);
        const invariant = inputs.filter(i => i.name === '__Invariant').map(i => i.value);
        assert.ok(invariant.includes(duration.name), `slot ${index} must bind its duration invariantly`);
        assert.ok(!invariant.some(name => name !== duration.name), 'removed indices must not survive in metadata');
    });
}

test('initial and added slots post invariant duration metadata', () => {
    const ui = slots();
    ui.add();
    ui.add();
    assert.equal(ui.rows.length, 3);
    assertFields(ui.rows);
});

test('removing a middle slot renumbers its duration metadata before another add', () => {
    const ui = slots();
    ui.add();
    ui.add();
    ui.rows[1].clickRemove();
    assert.equal(ui.rows.length, 2);
    assertFields(ui.rows);
    ui.add();
    assert.equal(ui.rows.length, 3);
    assertFields(ui.rows);
});
