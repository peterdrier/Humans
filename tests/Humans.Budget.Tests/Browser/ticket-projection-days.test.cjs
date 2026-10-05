const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const view = readFileSync(resolve(__dirname,
    '../../../src/Sections/Humans.Budget/Views/BudgetAdmin/YearDetail.cshtml'), 'utf8');
const script = [...view.matchAll(/<script>([\s\S]*?)<\/script>/g)]
    .find(match => match[1].includes('var actualSold = @actualTicketsSold;'))[1]
    .replace('var actualSold = @actualTicketsSold;', 'var actualSold = 100;');

for (const [now, eventDate, remaining] of [
    ['2026-03-28T12:00:00Z', '2026-03-30', 2],
    ['2026-03-28T23:30:00Z', '2026-03-30', 2],
    ['2026-03-10T12:00:00Z', '2026-03-13', 3],
    ['2026-03-28T12:00:00Z', '2026-03-27', 0],
]) {
    test(`projection uses ${remaining} UTC calendar days from ${now} to ${eventDate}`, () => {
        const previousTimezone = process.env.TZ;
        process.env.TZ = 'Europe/Madrid';
        try {
            class FixedDate extends Date {
                constructor(...args) { super(...(args.length ? args : [now])); }
            }
            const elements = {
                'proj-dailySalesRate': { value: '10' }, 'proj-totalTickets': { value: '' },
                'proj-startDate': { value: '2026-01-01' }, 'proj-eventDate': { value: eventDate },
                'proj-ticketBreakdown': { innerHTML: '' },
            };
            for (const element of Object.values(elements)) {
                element.addEventListener = (event, handler) => { element[event] = handler; };
            }
            vm.runInNewContext(script, { Date: FixedDate,
                document: { getElementById: id => elements[id] } });
            elements['proj-dailySalesRate'].input.call(elements['proj-dailySalesRate']);
            assert.equal(elements['proj-totalTickets'].value, 100 + remaining * 10);
            assert.ok(elements['proj-ticketBreakdown'].innerHTML.includes(` ${remaining} days`));
        } finally {
            if (previousTimezone === undefined) delete process.env.TZ;
            else process.env.TZ = previousTimezone;
        }
    });
}
