const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const site = readFileSync(resolve(__dirname, '../../../src/Humans.Web/wwwroot/js/site.js'), 'utf8');
const script = site.slice(site.indexOf('function showToast('), site.indexOf('// Initialize Bootstrap tooltips'));
for (const culture of ['', 'es', 'de', 'it', 'fr', 'ca']) {
    test(`dynamic toast close button uses the rendered label (${culture || 'en'})`, () => {
        const resx = readFileSync(resolve(__dirname,
            `../../../src/Humans.Base/Resources/SharedResource${culture ? '.' + culture : ''}.resx`), 'utf8');
        const label = resx.match(/<data name="Common_Close"[^>]*>\s*<value>([^<]*)<\/value>/)[1];
        function element() {
            return { attributes: {}, children: [],
                setAttribute(name, value) { this.attributes[name] = value; },
                appendChild(child) { this.children.push(child); }, addEventListener() {} };
        }
        const container = element();
        container.dataset = { closeLabel: label };
        const context = {
            document: { getElementById: () => container, createElement: element, createTextNode: text => ({ textContent: text }) },
            bootstrap: { Toast: class { show() {} } },
        };
        vm.createContext(context); vm.runInContext(script, context); context.showToast('Message', 'danger');
        const close = container.children[0].children[0].children[1];
        assert.equal(close.attributes['aria-label'], label);
    });
}
