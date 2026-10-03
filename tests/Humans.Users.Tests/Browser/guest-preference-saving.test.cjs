const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const view = fs.readFileSync(path.resolve(__dirname, '../../../src/Sections/Humans.Users/Views/GuestAccount/CommunicationPreferences.cshtml'), 'utf8');
const script = view.slice(view.indexOf('<script>') + 8, view.indexOf('</script>'))
    .replace(/^\s*@if.*Model.UnsubscribeToken.*\n\s*\{\n\s*@:body.append.*\n\s*\}/gm, '');
const flush = () => new Promise(resolve => setImmediate(resolve));
function page() {
    const requests = [];
    const rows = ['Marketing', 'Governance'].map(category => {
        const row = { dataset: { categoryRow: category }, classList: { add() {}, remove() {} } };
        const controls = ['email', 'alert'].map(channel => {
            const cb = { checked: true, disabled: false, dataset: { category, channel }, closest: () => row,
                addEventListener: (name, handler) => cb.change = () => {
                    if (cb.disabled) return;
                    cb.checked = !cb.checked;
                    handler.call(cb);
                } };
            return cb;
        });
        row.controls = controls;
        row.querySelector = selector => controls[selector.includes('email') ? 0 : 1];
        row.querySelectorAll = () => controls;
        return row;
    });
    const banner = { dataset: { category: 'Marketing' }, classList: { add() {}, remove() {} } };
    const unsubscribe = { disabled: false, addEventListener: (name, handler) => unsubscribe.click = () => {
        if (!unsubscribe.disabled) handler.call(unsubscribe);
    } };
    const document = {
        querySelectorAll: () => rows.flatMap(r => r.controls),
        querySelector: selector => selector.includes('RequestVerificationToken') ? { value: 'token' }
            : rows.find(r => selector.includes(r.dataset.categoryRow)),
        getElementById: id => id === 'oneClickUnsubscribe' ? unsubscribe : banner,
    };
    vm.runInNewContext(script, { document, URLSearchParams, setTimeout() {}, fetch: (url, options) => {
        let resolve;
        const promise = new Promise(yes => resolve = yes);
        requests.push({ body: new URLSearchParams(options.body), resolve });
        return promise;
    } });
    return { rows, banner, unsubscribe, requests, reply: (index, ok = true) => requests[index].resolve({ ok }) };
}
test('email and alert writes cannot overlap in one guest preference row', async () => {
    const ui = page();
    const [email, alert] = ui.rows[0].controls;
    email.change(); alert.change();
    assert.equal(ui.requests.length, 1);
    assert.equal(alert.checked, true);
    ui.reply(0); await flush();
    assert.equal(email.disabled, false);
    assert.equal(alert.disabled, false);
    alert.change();
    assert.equal(ui.requests.length, 2);
    assert.equal(ui.requests[1].body.get('emailEnabled'), 'false');
    assert.equal(ui.requests[1].body.get('alertEnabled'), 'false');
    ui.reply(1); await flush();
});
test('one-click unsubscribe prevents a simultaneous matrix update', async () => {
    const ui = page();
    ui.unsubscribe.click(); ui.rows[0].controls[1].change();
    assert.equal(ui.requests.length, 1);
    ui.reply(0); await flush();
    assert.equal(ui.rows[0].controls[0].checked, false);
    assert.equal(ui.rows[0].controls[1].checked, true);
    assert.equal(ui.rows[0].controls[1].disabled, false);
});
test('matrix update disables the matching one-click unsubscribe action', async () => {
    const ui = page();
    ui.rows[0].controls[1].change(); ui.unsubscribe.click();
    assert.equal(ui.requests.length, 1);
    assert.equal(ui.unsubscribe.disabled, true);
    ui.reply(0); await flush();
    assert.equal(ui.unsubscribe.disabled, false);
    ui.unsubscribe.click();
    assert.equal(ui.requests[1].body.get('alertEnabled'), 'false');
    ui.reply(1); await flush();
});
test('failed matrix update restores its value and unlocks the row and banner', async () => {
    const ui = page();
    const [email, alert] = ui.rows[0].controls;
    email.change(); ui.reply(0, false); await flush();
    assert.equal(email.checked, true);
    assert.equal(alert.checked, true);
    assert.equal(email.disabled, false);
    assert.equal(alert.disabled, false);
    assert.equal(ui.unsubscribe.disabled, false);
});
test('another category can save independently of a pending row', async () => {
    const ui = page();
    ui.rows[1].controls[0].change();
    assert.equal(ui.rows[0].controls[0].disabled, false);
    assert.equal(ui.unsubscribe.disabled, false);
    ui.rows[0].controls[1].change();
    assert.equal(ui.requests.length, 2);
    assert.equal(ui.requests[0].body.get('category'), 'Governance');
    assert.equal(ui.requests[1].body.get('category'), 'Marketing');
    ui.reply(1); ui.reply(0); await flush();
});
