const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const script = fs.readFileSync(path.resolve(__dirname,
    '../../../src/Sections/Humans.Scanner/wwwroot/js/scanner/tickets.js'), 'utf8')
    .replace(/^import .*;\s*/m, '').replace('export function', 'function');
const settle = () => new Promise(setImmediate);
function deferred() {
    let resolve, reject;
    const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
    return { promise, resolve, reject };
}
function scanner() {
    let submit, hit;
    const requests = [];
    const attributes = {};
    const card = {
        innerHTML: '<article>Previous person</article>',
        replaceChildren(...children) { this.innerHTML = children.length ? children[0].textContent : ''; },
        setAttribute: (key, value) => attributes[key] = value,
    };
    const manualInput = { value: 'second-code' };
    const refs = {
        card, cardUrl: '/Scanner/Card', manualInput,
        manualForm: { addEventListener: (_, handler) => submit = handler },
        labels: { lookupFailed: 'Localized lookup failure' },
    };
    vm.runInNewContext(script + '\ninitTicketScanner(refs);', {
        refs, console: { error() {} }, initBarcodeScanner: options => hit = options.onHit,
        document: { createElement: () => ({ setAttribute() {}, textContent: '' }) },
        fetch: url => { const result = deferred(); requests.push({ ...result, url }); return result.promise; },
    });
    return { card, attributes, requests, camera: value => hit(value), manual: () => submit({ preventDefault() {} }) };
}
const response = html => ({ ok: true, text: () => Promise.resolve(html) });

for (const source of ['manual', 'camera']) {
    test(`${source} lookup clears the previous ticket while waiting`, async () => {
        const s = scanner();
        if (source === 'manual') s.manual(); else void s.camera('second-code');
        assert.equal(s.card.innerHTML, '');
        assert.equal(s.attributes['aria-busy'], 'true');
        s.requests[0].resolve(response('<article>Second person</article>'));
        await settle();
        assert.equal(s.card.innerHTML, '<article>Second person</article>');
        assert.equal(s.attributes['aria-busy'], 'false');
    });
}

test('older lookup cannot populate the card or release a newer pending lookup', async () => {
    const s = scanner();
    void s.camera('first');
    void s.camera('second');
    s.requests[0].resolve(response('Old person'));
    await settle();
    assert.equal(s.card.innerHTML, '');
    assert.equal(s.attributes['aria-busy'], 'true');
    s.requests[1].resolve(response('New person'));
    await settle();
    assert.equal(s.card.innerHTML, 'New person');
    assert.equal(s.attributes['aria-busy'], 'false');
});

for (const network of [false, true]) {
    test(`${network ? 'network' : 'HTTP'} failure releases busy state and shows the latest failure`, async () => {
        const s = scanner();
        void s.camera('second');
        if (network) s.requests[0].reject(new Error('Offline'));
        else s.requests[0].resolve({ ok: false });
        await settle();
        assert.equal(s.card.innerHTML, 'Localized lookup failure');
        assert.equal(s.attributes['aria-busy'], 'false');
    });
}

test('lookup remains busy until its response body has finished loading', async () => {
    const s = scanner(), body = deferred();
    void s.camera('second');
    s.requests[0].resolve({ ok: true, text: () => body.promise });
    await settle();
    assert.equal(s.card.innerHTML, '');
    assert.equal(s.attributes['aria-busy'], 'true');
    body.resolve('Second person');
    await settle();
    assert.equal(s.card.innerHTML, 'Second person');
    assert.equal(s.attributes['aria-busy'], 'false');
});
