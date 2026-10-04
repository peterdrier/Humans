const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.resolve(__dirname, '../../..');
const view = fs.readFileSync(path.join(root, 'src/Sections/Humans.Camps/Views/Camp/_LinksEditorScripts.cshtml'), 'utf8');
function editor(label, count = 0) {
    const handlers = {};
    const rows = [];
    const container = { querySelectorAll: () => Array(count + rows.length), appendChild: row => rows.push(row) };
    const script = view.replace(/@Html.Raw\(System.Text.Json.JsonSerializer.Serialize\(SharedLocalizer\["Common_Remove"\].Value\)\)/g, JSON.stringify(label))
        .replace(/<\/?script>/g, '');
    const document = {
        getElementById: id => id === 'links-container' ? container : { addEventListener: (name, handler) => handlers.add = handler },
        addEventListener: (name, handler) => handlers.remove = handler,
        createElement: () => {
            const attributes = {};
            const button = { setAttribute: (name, value) => attributes[name] = value };
            return { attributes, querySelector: () => button, remove() { rows.splice(rows.indexOf(this), 1); } };
        },
    };
    vm.runInNewContext(script, { document });
    return { rows, add: () => handlers.add(), remove: row => handlers.remove({ target: { closest: selector => selector === '.remove-link' ? {} : row } }) };
}
for (const culture of ['en', 'es', 'de', 'it', 'fr', 'ca']) {
    const resource = fs.readFileSync(path.join(root, `src/Humans.Base/Resources/SharedResource${culture === 'en' ? '' : '.' + culture}.resx`), 'utf8');
    const label = resource.match(/<data name="Common_Remove"[^>]*><value>(.*?)<\/value>/s)[1];
    test(`added camp link has a translated accessible remove name in ${culture}`, () => {
        const form = editor(label);
        form.add();
        assert.equal(form.rows.length, 1);
        assert.equal(form.rows[0].attributes['aria-label'], label);
        form.remove(form.rows[0]);
        assert.equal(form.rows.length, 0);
    });
}
test('link editor retains the ten-link limit', () => {
    const form = editor('Remove', 9);
    form.add();
    form.add();
    assert.equal(form.rows.length, 1);
});
