const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

for (const view of ['Events/IndividualEventForm', 'EventsModeration/AdminEventForm']) {
    const source = readFileSync(resolve(__dirname,
        `../../../src/Sections/Humans.Events/Views/${view}.cshtml`), 'utf8');
    const script = source.match(/<script>([\s\S]*?)<\/script>/)[1];
    for (const initiallyAllDay of [false, true]) {
        test(`${view} preserves a valid duration through all-day toggles (initially ${initiallyAllDay})`, () => {
            const handlers = {};
            let duration = '90';
            const elements = {
                IsAllDay: {
                    checked: initiallyAllDay,
                    addEventListener: (event, handler) => { handlers[event] = handler; },
                },
                durationSelect: {
                    get value() { return duration; },
                    // A select clears its value when assigned a nonexistent option.
                    set value(value) { duration = ['15', '60', '90', '480'].includes(value) ? value : ''; },
                },
                startTimeField: { style: {}, querySelector: () => null },
                durationField: { style: {} },
            };
            vm.runInNewContext(script, {
                document: {
                    getElementById: id => elements[id] ?? null,
                    querySelectorAll: () => [],
                    querySelector: () => null,
                },
            });
            assert.equal(duration, '90');
            elements.IsAllDay.checked = true;
            handlers.change();
            assert.equal(elements.durationField.style.display, 'none');
            assert.equal(duration, '90');
            elements.IsAllDay.checked = false;
            handlers.change();
            assert.equal(elements.durationField.style.display, '');
            assert.equal(elements.startTimeField.style.display, '');
            assert.equal(duration, '90');
        });
    }
}
