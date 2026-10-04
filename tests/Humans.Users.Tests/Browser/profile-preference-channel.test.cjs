const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const view = fs.readFileSync(path.resolve(__dirname, '../../../src/Sections/Humans.Users/Views/Profile/CommunicationPreferences.cshtml'), 'utf8');
const script = view.slice(view.indexOf('<script>') + 8, view.indexOf('</script>'));
const flush = () => new Promise(resolve => setImmediate(resolve));
function form(alertEnabled) {
    let change, resolve;
    const requests = [];
    const row = { dataset: { alertEnabled: String(alertEnabled) }, querySelector: selector => selector.includes('email') ? email : null,
        classList: { add() {}, remove() {} } };
    const email = { checked: true, disabled: false, dataset: { category: 'Marketing', channel: 'email' }, closest: () => row,
        addEventListener: (name, handler) => change = () => handler.call(email) };
    vm.runInNewContext(script, { URLSearchParams, setTimeout() {},
        document: { querySelectorAll: () => [email], querySelector: () => ({ value: 'token' }) },
        fetch: (url, options) => { requests.push(new URLSearchParams(options.body)); return new Promise(yes => resolve = yes); },
    });
    return { email, requests, change: () => { email.checked = !email.checked; change(); }, reply: ok => resolve({ ok }) };
}
for (const enabled of [false, true]) {
    test(`changing email preserves the existing inbox setting ${enabled}`, async () => {
        const ui = form(enabled);
        ui.change();
        assert.equal(ui.requests[0].get('emailEnabled'), 'false');
        assert.equal(ui.requests[0].get('alertEnabled'), String(enabled));
        assert.equal(ui.requests[0].get('category'), 'Marketing');
        ui.reply(true); await flush();
        assert.equal(ui.email.disabled, false);
        assert.equal(ui.email.checked, false);
    });
}
test('failed email update restores the checkbox without changing the inbox setting', async () => {
    const ui = form(false);
    ui.change(); ui.reply(false); await flush();
    assert.equal(ui.email.checked, true);
    assert.equal(ui.email.disabled, false);
    assert.equal(ui.requests[0].get('alertEnabled'), 'false');
});
