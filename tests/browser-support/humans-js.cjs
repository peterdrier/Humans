// Gives a vm sandbox the real window.Humans helpers from site.js, so harnesses exercise
// the shared csrf/escape/post code instead of stubbing it per page.
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const site = readFileSync(resolve(__dirname, '../../src/Humans.Web/wwwroot/js/site.js'), 'utf8');
const namespaceSource = site.slice(0, site.indexOf('// Cookie consent banner'));

// document, fetch, FormData and URLSearchParams resolve against the sandbox at call time, so a
// harness may define them before or after this call. The sandbox's document only has to
// answer the page's other lookups: the antiforgery input is answered here.
function installHumans(sandbox, { token = 'csrf' } = {}) {
    const host = {};
    const tokenInput = { value: token };
    Object.defineProperties(host, {
        document: {
            enumerable: true,
            get: () => {
                const doc = sandbox.document ?? {};
                return new Proxy(doc, {
                    get: (target, name) => name === 'querySelector'
                        ? selector => String(selector).includes('__RequestVerificationToken')
                            ? tokenInput : target.querySelector?.(selector)
                        : target[name],
                });
            },
        },
        fetch: { enumerable: true, get: () => sandbox.fetch },
        FormData: { enumerable: true, get: () => sandbox.FormData ?? FormData },
        URLSearchParams: { enumerable: true, get: () => sandbox.URLSearchParams ?? URLSearchParams },
    });
    host.window = host;
    vm.runInNewContext(namespaceSource, host);
    sandbox.Humans = host.Humans;
    return sandbox;
}

module.exports = { installHumans, namespaceSource };
