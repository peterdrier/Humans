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

for (const view of ['Events/IndividualEventForm', 'Events/BarrioEventForm', 'EventsModeration/AdminEventForm']) {
    test(`${view} updates remaining title and description counts while typing`, () => {
        const viewsRoot = resolve(__dirname, '../../../src/Sections/Humans.Events/Views');
        let source = readFileSync(resolve(viewsRoot, `${view}.cshtml`), 'utf8');
        source = source.replace(/<partial name="(_EventCharacterCounters)"\s*\/>/g,
            (_, name) => readFileSync(resolve(viewsRoot, 'Shared', `${name}.cshtml`), 'utf8'));
        const elements = {};
        const fields = {};
        for (const [prefix, field, limit] of [['title', 'Title', 80], ['description', 'Description', 450]]) {
            const markup = source.match(new RegExp(`<(?:input|textarea)[^>]*asp-for="${field}"[^>]*>`))[0];
            const id = markup.match(/id="([^"]+)"/)?.[1] ?? field;
            const input = { value: '', maxLength: Number(markup.match(/maxlength="(\d+)"/)[1]),
                addEventListener(event, handler) { this[event] = handler; } };
            const counter = { textContent: String(limit) };
            elements[id] = input;
            if (source.includes(`id="${prefix}Counter"`)) elements[`${prefix}Counter`] = counter;
            fields[prefix] = { input, counter };
        }
        const context = { document: {
            getElementById: id => elements[id] ?? null,
            querySelectorAll: () => [], querySelector: () => null,
        } };
        for (const match of source.matchAll(/<script>([\s\S]*?)<\/script>/g)) {
            vm.runInNewContext(match[1], context);
        }
        for (const [prefix, limit] of [['title', 80], ['description', 450]]) {
            const { input, counter } = fields[prefix];
            input.value = 'Tea 😀';
            input.input?.();
            assert.equal(Number(counter.textContent), limit - input.value.length);
            input.value = '';
            input.input?.();
            assert.equal(Number(counter.textContent), limit);
        }
    });
}
