const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

function page(errorLabel = 'Notification error') {
    const source = readFileSync(resolve(__dirname, '../../../src/Humans.Web/wwwroot/js/site.js'), 'utf8');
    const start = source.indexOf('// Notification bell popup');
    const end = source.indexOf('// Show a Bootstrap toast notification', start);
    assert.ok(start >= 0 && end > start);
    const events = {};
    const button = {
        setAttribute() {}, focus() {},
        getAttribute: name => name === 'data-load-error' ? errorLabel : null,
        addEventListener: (name, handler) => { events[name] = handler; },
    };
    const popup = { style: {}, querySelectorAll: () => [], addEventListener() {} };
    const errorText = { textContent: '' };
    const content = { innerHTML: '', querySelector: () => errorText };
    const elements = {
        notificationBellWrapper: { contains: () => false }, notificationBellBtn: button,
        notificationPopup: popup, notificationPopupContent: content,
    };
    const requests = [];
    const context = {
        document: { getElementById: id => elements[id] ?? null, addEventListener() {} },
        fetch: () => new Promise((resolveResponse, reject) => requests.push({ resolveResponse, reject })),
    };
    vm.createContext(context);
    vm.runInContext(source.slice(start, end), context);
    return {
        content, popup, errorText,
        click: () => events.click({ stopPropagation() {} }),
        respond: async (index, html, fail = false) => {
            if (fail) requests[index].reject(new Error('offline'));
            else requests[index].resolveResponse({ ok: true, text: async () => html });
            await new Promise(resolveDone => setImmediate(resolveDone));
        },
    };
}

for (const fail of [false, true]) {
    test(`an older popup ${fail ? 'error' : 'response'} cannot replace a newer inbox`, async () => {
        const ui = page();
        ui.click(); ui.click(); ui.click();
        await ui.respond(1, '<p>New inbox</p>');
        await ui.respond(0, '<p>Old inbox</p>', fail);
        assert.equal(ui.content.innerHTML, '<p>New inbox</p>');
    });
}

test('a closed popup ignores its late response', async () => {
    const ui = page(); ui.click(); ui.click();
    const pending = ui.content.innerHTML;
    await ui.respond(0, '<p>Closed inbox</p>');
    assert.equal(ui.popup.style.display, 'none');
    assert.equal(ui.content.innerHTML, pending);
});

test('an old response cannot replace a reopened popup loading state', async () => {
    const ui = page(); ui.click(); ui.click(); ui.click();
    const pending = ui.content.innerHTML;
    await ui.respond(0, '<p>Old inbox</p>');
    assert.equal(ui.content.innerHTML, pending);
    await ui.respond(1, '<p>New inbox</p>');
    assert.equal(ui.content.innerHTML, '<p>New inbox</p>');
});

test('a current popup failure renders the supplied message as text', async () => {
    const message = 'No se pudieron cargar las notificaciones. <b>Inténtalo de nuevo.</b>';
    const ui = page(message); ui.click();
    await ui.respond(0, '', true);
    assert.equal(ui.errorText.textContent, message);
    assert.ok(!ui.content.innerHTML.includes(message));
});
