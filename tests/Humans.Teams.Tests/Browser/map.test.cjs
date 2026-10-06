const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const source = readFileSync(resolve(__dirname,
    '../../../src/Sections/Humans.Teams/Views/Team/Map.cshtml'), 'utf8');
const start = source.indexOf('function avatarHtml(');
const end = source.indexOf('function personHtml(', start);
assert.ok(start >= 0 && end > start);
const context = {
    escapeHtml: text => String(text).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;'),
    clrMutedText: '#666', clrTextOnDark: '#fff',
};
vm.createContext(context); vm.runInContext(source.slice(start, end), context);

for (const [name, expected] of [['😀 Alice', '😀A'], ['e\u0301 Bob', 'E\u0301B'],
    ['👩‍👩‍👧‍👦 Family', '👩‍👩‍👧‍👦F'], ['Alice  Bob Carol', 'AB'], ['', '?'], ['<script Bob', '&lt;B']]) {
    test(`map avatar preserves two word initials of ${JSON.stringify(name)}`, () => {
        const html = context.avatarHtml({ displayName: name }, 40);
        assert.ok(html.endsWith(`>${expected}</div>`), html);
    });
}
