const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync, readdirSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

for (const page of ['Budget/Summary', 'BudgetAdmin/YearDetail']) {
    const view = readFileSync(resolve(__dirname,
        `../../../src/Sections/Humans.Budget/Views/${page}.cshtml`), 'utf8');
    const start = view.indexOf('var tooltipCallback =');
    const end = view.indexOf('var legendOpts =', start);
    assert.ok(start >= 0 && end > start, `${page} contains the live chart callback`);
    const script = view.slice(start, end);
    const languages = readdirSync(resolve(__dirname, '../../../src/Sections/Humans.Budget'))
        .map(file => /^BudgetResource\.([\w-]+)\.resx$/.exec(file)?.[1]).filter(Boolean);
    const cases = [['en', '1,234.50'], ['', '1,234.50']].concat(languages.map(language => [language,
        (1234.5).toLocaleString(language, { minimumFractionDigits: 2, maximumFractionDigits: 2 })]));
    for (const [language, amount] of cases) {
        test(`${page} money tooltip follows the document language (${language || 'fallback'})`, () => {
            const context = { document: { documentElement: { lang: language } } };
            vm.createContext(context);
            vm.runInContext(script, context);

            assert.equal(context.tooltipCallback.label({ label: 'Tickets', raw: 1234.5 }), `Tickets: €${amount}`);
        });
    }
}
