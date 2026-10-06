const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const view = readFileSync(resolve(__dirname,
    '../../../src/Sections/Humans.Calendar/Views/Calendar/_CalendarEventFormFields.cshtml'), 'utf8');
const script = view.match(/<script[^>]*>([\s\S]*?)<\/script>/)[1];

function picker() {
    const fields = {};
    for (const id of ['rec-freq', 'rec-interval', 'rec-until', 'rec-count', 'rec-month-nth',
        'rec-month-day', 'rec-ends-on', 'rec-ends-after', 'rec-ends-never', 'RecurrenceRule',
        'calendar-event-isallday', 'StartLocal', 'StartDateLocal', 'rec-month-day-label', 'rec-month-nth-label']) {
        fields[id] = { value: '', checked: false, disabled: false,
            addEventListener() {} };
    }
    fields['rec-interval'].value = '1';
    fields['rec-count'].value = '10';
    fields.StartLocal.value = '2026-06-01T19:00';
    const recurring = { value: 'false' };
    const hidden = { classList: { toggle() {} } };
    const form = { querySelector: () => recurring,
        addEventListener(event, handler) { this[event] = handler; } };
    const root = { closest: () => form, contains: () => true,
        querySelectorAll: () => [], querySelector: () => hidden,
        dataset: { dayNames: 'Sun|Mon|Tue|Wed|Thu|Fri|Sat', monthDay: '{0}',
            nth1: '{0}', nth2: '{0}', nth3: '{0}', nth4: '{0}', nthLast: '{0}' } };
    vm.runInNewContext(script, { document: {
        getElementById: id => id === 'calendar-recurrence' ? root : fields[id],
    } });
    return { fields, recurring, change: () => form.change({ target: fields['rec-freq'] }) };
}

for (const inactiveFrequency of ['NONE', 'CUSTOM']) {
    test(`switching to ${inactiveFrequency} removes hidden numeric constraints without losing entered values`, () => {
        const ui = picker();
        ui.fields['rec-freq'].value = 'DAILY';
        ui.fields['rec-ends-after'].checked = true;
        ui.fields['rec-interval'].value = '0';
        ui.fields['rec-count'].value = '0';
        ui.change();
        assert.equal(ui.fields['rec-interval'].disabled, false);
        assert.equal(ui.fields['rec-count'].disabled, false);
        ui.fields['rec-freq'].value = inactiveFrequency;
        ui.change();
        assert.equal(ui.fields['rec-interval'].disabled, true);
        assert.equal(ui.fields['rec-count'].disabled, true);
        assert.equal(ui.fields['rec-interval'].value, '0');
        assert.equal(ui.fields['rec-count'].value, '0');
        ui.fields['rec-freq'].value = 'DAILY';
        ui.change();
        assert.equal(ui.fields['rec-interval'].disabled, false);
        assert.equal(ui.fields['rec-count'].disabled, false);
    });
}

test('only the selected recurrence end condition participates in form validation', () => {
    const ui = picker();
    ui.fields['rec-freq'].value = 'WEEKLY';
    ui.fields['rec-ends-on'].checked = true;
    ui.change();
    assert.equal(ui.fields['rec-until'].disabled, false);
    assert.equal(ui.fields['rec-until'].required, true);
    assert.equal(ui.fields['rec-count'].disabled, true);
    ui.fields['rec-ends-on'].checked = false;
    ui.fields['rec-ends-after'].checked = true;
    ui.change();
    assert.equal(ui.fields['rec-until'].disabled, true);
    assert.equal(ui.fields['rec-until'].required, false);
    assert.equal(ui.fields['rec-count'].disabled, false);
    assert.equal(ui.fields['rec-count'].required, true);
    ui.fields['rec-ends-after'].checked = false;
    ui.change();
    assert.equal(ui.fields['rec-count'].disabled, true);
    assert.equal(ui.fields['rec-count'].required, false);
});
