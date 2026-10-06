const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const view = fs.readFileSync(path.resolve(__dirname, '../../../src/Sections/Humans.GoogleIntegration/Views/Google/Sync.cshtml'), 'utf8');
const script = view.slice(view.indexOf('(function ()'), view.indexOf('</script>'))
    .replace(/^.*@await Html.PartialAsync.*$/m, '');
const flush = () => new Promise(resolve => setImmediate(resolve));
function deferred() {
    let resolve, reject;
    const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
    return { promise, resolve, reject };
}
function dashboard() {
    const requests = [];
    const panes = {};
    const tabs = ['DriveFolder', 'Group'].map(type => {
        const pane = { innerHTML: '', querySelector: () => null, querySelectorAll: () => [] };
        panes[type] = pane;
        const tab = { getAttribute: () => type, addEventListener: (name, handler) => tab.show = () => handler.call(tab) };
        return tab;
    });
    const document = {
        querySelectorAll: () => tabs,
        querySelector: () => null,
        getElementById: id => panes[id === 'drives-tab' ? 'DriveFolder' : 'Group'],
        addEventListener() {},
    };
    vm.runInNewContext(script, { document, getRequestVerificationToken: () => 'token', fetch: (url, options) => {
        const request = deferred();
        requests.push({ ...request, url, options });
        return request.promise;
    } });
    return { panes, requests, load: type => tabs[type === 'DriveFolder' ? 0 : 1].show(),
        reply: (index, html) => requests[index].resolve({ ok: true, text: async () => html }) };
}
test('older sync preview cannot replace the latest completed preview', async () => {
    const ui = dashboard();
    ui.load('DriveFolder'); ui.load('DriveFolder');
    ui.reply(1, 'current'); await flush();
    ui.reply(0, 'old'); await flush();
    assert.equal(ui.panes.DriveFolder.innerHTML, 'current');
});
test('older preview failure cannot replace the current preview', async () => {
    const ui = dashboard();
    ui.load('Group'); ui.load('Group');
    ui.reply(1, 'current'); await flush();
    ui.requests[0].reject(new Error('old failure')); await flush();
    assert.equal(ui.panes.Group.innerHTML, 'current');
});
test('new preview failure stays visible when an older success completes', async () => {
    const ui = dashboard();
    ui.load('DriveFolder'); ui.load('DriveFolder');
    ui.requests[1].reject(new Error('current failure')); await flush();
    const failure = ui.panes.DriveFolder.innerHTML;
    assert.match(failure, /current failure/);
    ui.reply(0, 'old'); await flush();
    assert.equal(ui.panes.DriveFolder.innerHTML, failure);
});
test('drive and group previews update independently', async () => {
    const ui = dashboard();
    ui.load('DriveFolder'); ui.load('Group');
    assert.equal(ui.requests[0].url, '/Google/Sync/Preview/0');
    assert.equal(ui.requests[1].url, '/Google/Sync/Preview/2');
    ui.reply(1, 'groups'); ui.reply(0, 'drives'); await flush();
    assert.equal(ui.panes.DriveFolder.innerHTML, 'drives');
    assert.equal(ui.panes.Group.innerHTML, 'groups');
});
test('preview that is still reading its body cannot overwrite a newer load', async () => {
    const ui = dashboard();
    const body = deferred();
    ui.load('Group');
    ui.requests[0].resolve({ ok: true, text: () => body.promise }); await flush();
    ui.load('Group'); ui.reply(1, 'current'); await flush();
    body.resolve('old'); await flush();
    assert.equal(ui.panes.Group.innerHTML, 'current');
});

test('a redirected login page is a preview failure and the tab can retry', async () => {
    const ui = dashboard();
    ui.load('DriveFolder');
    if (ui.requests[0].options?.redirect === 'error') ui.requests[0].reject(new TypeError('redirect blocked'));
    else ui.reply(0, '<form>Login</form>');
    await flush();
    assert.match(ui.panes.DriveFolder.innerHTML, /Failed to load sync data/);
    assert.doesNotMatch(ui.panes.DriveFolder.innerHTML, /<form>Login/);
    ui.load('DriveFolder');
    ui.reply(1, 'current'); await flush();
    assert.equal(ui.panes.DriveFolder.innerHTML, 'current');
});
