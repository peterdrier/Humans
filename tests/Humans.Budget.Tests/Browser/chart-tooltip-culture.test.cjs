const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

for (const page of ['Budget/Summary', 'BudgetAdmin/YearDetail']) {
    const view = readFileSync(resolve(__dirname,
        `../../../src/Sections/Humans.Budget/Views/${page}.cshtml`), 'utf8');
    const start = view.indexOf('var tooltipCallback =');
    const end = view.indexOf('var legendOpts =', start);
    assert.ok(start >= 0 && end > start, `${page} contains the live chart callback`);
    const script = view.slice(start, end);
    for (const [language, amount] of [
        ['en', '1,234.50'], ['es', '1234,50'], ['de', '1.234,50'],
        ['it', '1234,50'], ['fr', '1\u202f234,50'], ['ca', '1.234,50'], ['', '1,234.50'],
    ]) {
        test(`${page} money tooltip follows the document language (${language || 'fallback'})`, () => {
            const context = { document: { documentElement: { lang: language } } };
            vm.createContext(context);
            vm.runInContext(script, context);

            assert.equal(context.tooltipCallback.label({ label: 'Tickets', raw: 1234.5 }), `Tickets: €${amount}`);
        });
    }
}
