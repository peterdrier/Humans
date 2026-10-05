const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const view = fs.readFileSync(path.resolve(__dirname, '../../../src/Sections/Humans.Users/Views/Profile/CommunicationPreferences.cshtml'), 'utf8');
const script = view.slice(view.indexOf('<script>') + 8, view.indexOf('</script>'));
const flush = () => new Promise(resolve => setImmediate(resolve));
function form(alertEnabled, showAlert = false) {
    let change, alertChange, resolve, reject, options;
    const requests = [];
    const classes = new Set();
    const row = { dataset: { alertEnabled: String(alertEnabled) }, querySelector: selector => selector.includes('email') ? email : showAlert ? alert : null,
        querySelectorAll: () => showAlert ? [email, alert] : [email],
        classList: { add: value => classes.add(value), remove: value => classes.delete(value) } };
    const email = { checked: true, disabled: false, dataset: { category: 'Marketing', channel: 'email' }, closest: () => row,
        addEventListener: (name, handler) => change = () => handler.call(email) };
    const alert = { checked: alertEnabled, disabled: false, dataset: { category: 'Marketing', channel: 'alert' }, closest: () => row,
        addEventListener: (name, handler) => alertChange = () => handler.call(alert) };
    vm.runInNewContext(script, { URLSearchParams, setTimeout() {},
        document: { querySelectorAll: () => showAlert ? [email, alert] : [email], querySelector: () => ({ value: 'token' }) },
        fetch: (url, requestOptions) => {
            options = requestOptions;
            requests.push(new URLSearchParams(options.body));
            return new Promise((yes, no) => { resolve = yes; reject = no; });
        },
    });
    return { email, alert, requests, classes, change: (channel = 'email') => {
        const control = channel === 'email' ? email : alert;
        if (control.disabled) return;
        control.checked = !control.checked;
        (channel === 'email' ? change : alertChange)();
    }, reply: ok => resolve({ ok }),
        redirect: () => options.redirect === 'error' ? reject(new TypeError('redirect blocked')) : resolve({ ok: true }) };
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

test('a redirected login page cannot confirm a preference save', async () => {
    const ui = form(false);
    ui.change(); ui.redirect(); await flush();
    assert.equal(ui.email.checked, true);
    assert.equal(ui.email.disabled, false);
    assert.equal(ui.classes.has('table-success'), false);
    assert.equal(ui.classes.has('table-danger'), true);
    assert.equal(ui.requests[0].get('alertEnabled'), 'false');
});

for (const ok of [true, false]) {
    test(`both member preference channels stay locked until a ${ok ? 'successful' : 'failed'} save completes`, async () => {
        const ui = form(true, true);
        ui.change(); ui.change('alert');
        assert.equal(ui.requests.length, 1);
        assert.equal(ui.email.disabled, true);
        assert.equal(ui.alert.disabled, true);
        assert.equal(ui.alert.checked, true);
        ui.reply(ok); await flush();
        assert.equal(ui.email.disabled, false);
        assert.equal(ui.alert.disabled, false);
        assert.equal(ui.email.checked, !ok);
        ui.change('alert');
        assert.equal(ui.requests.length, 2);
        assert.equal(ui.requests[1].get('emailEnabled'), String(!ok));
        assert.equal(ui.requests[1].get('alertEnabled'), 'false');
        ui.reply(true); await flush();
    });
}
