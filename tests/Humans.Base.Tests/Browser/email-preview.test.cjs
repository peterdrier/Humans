const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

function page() {
    const classes = () => {
        const values = new Set(['d-none']);
        return { add: value => values.add(value), remove: value => values.delete(value), contains: value => values.has(value) };
    };
    const elements = {};
    for (const id of ['one', 'two']) {
        elements[id] = {};
        elements[id + 'Frame'] = { srcdoc: '' };
        elements[id + 'Error'] = { textContent: '', classList: classes() };
        const span = { textContent: '' };
        elements[id + 'Subject'] = { classList: classes(), querySelector: () => span };
    }
    const constants = {
        loadingHtml: 'Loading', errorText: 'Preview failed', previewUrl: '/preview', selfSendUrl: '/self',
        selfSentText: 'Sent', selfErrorText: 'Failed', selfRateLimitedText: 'Limited',
        selfNoAddressText: 'No address', confirmLargeSendText: 'Confirm',
    };
    const source = readFileSync(resolve(__dirname,
        '../../../src/Humans.Base/Views/Shared/_EmailPreviewModal.cshtml'), 'utf8');
    const script = source.slice(source.indexOf('(function () {'), source.indexOf('</script>'))
        .replace(/^    var (\w+) = @Html.Raw\(.*\);$/gm, (_, name) => {
            assert.ok(Object.hasOwn(constants, name));
            return `    var ${name} = ${JSON.stringify(constants[name])};`;
        });
    const requests = [];
    const context = {
        window: {}, FormData, getRequestVerificationToken: () => 'token',
        bootstrap: { Modal: { getOrCreateInstance: () => ({ show() {} }) } },
        document: { getElementById: id => elements[id] },
        fetch: () => new Promise((resolveResponse, reject) => requests.push({ resolveResponse, reject })),
    };
    vm.createContext(context);
    const install = () => vm.runInContext(script, context);
    install();
    return {
        requests, elements, install,
        preview: (id, subject) => context.window.HumansEmailComposer.preview(subject, subject + ' body', '', id),
        respond: async (index, subject, fail = false) => {
            if (fail) requests[index].reject(new Error('offline'));
            else requests[index].resolveResponse({ ok: true, json: async () => ({ html: subject + ' HTML', subject }) });
            await new Promise(resolveDone => setImmediate(resolveDone));
        },
    };
}

test('an older email preview cannot overwrite a newer body or subject', async () => {
    const ui = page(); ui.preview('one', 'Old'); ui.preview('one', 'New');
    await ui.respond(1, 'New'); await ui.respond(0, 'Old');
    assert.equal(ui.elements.oneFrame.srcdoc, 'New HTML');
    assert.equal(ui.elements.oneSubject.querySelector('span').textContent, 'New');
});

test('an older preview error cannot appear over a newer success', async () => {
    const ui = page(); ui.preview('one', 'Old'); ui.preview('one', 'New');
    await ui.respond(1, 'New'); await ui.respond(0, '', true);
    assert.equal(ui.elements.oneError.classList.contains('d-none'), true);
    assert.equal(ui.elements.oneFrame.srcdoc, 'New HTML');
});

test('two preview modals retain independent response ordering', async () => {
    const ui = page(); ui.preview('one', 'First'); ui.preview('two', 'Second');
    await ui.respond(1, 'Second'); await ui.respond(0, 'First');
    assert.equal(ui.elements.oneFrame.srcdoc, 'First HTML');
    assert.equal(ui.elements.twoFrame.srcdoc, 'Second HTML');
});

test('the current preview still displays its own localized failure', async () => {
    const ui = page(); ui.preview('one', 'Draft'); await ui.respond(0, '', true);
    assert.equal(ui.elements.oneError.classList.contains('d-none'), false);
    assert.equal(ui.elements.oneError.textContent, 'Preview failed');
});

test('reinstalling a composer script cannot revive an older modal preview', async () => {
    const ui = page(); ui.preview('one', 'Old'); ui.install(); ui.preview('one', 'New');
    await ui.respond(1, 'New'); await ui.respond(0, 'Old');
    assert.equal(ui.elements.oneFrame.srcdoc, 'New HTML');
});
