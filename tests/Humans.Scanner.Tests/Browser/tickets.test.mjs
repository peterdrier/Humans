import assert from 'node:assert/strict';
import { test } from 'node:test';
import { initTicketScanner } from '../../../src/Sections/Humans.Scanner/wwwroot/js/scanner/tickets.js';

function deferred() {
    let resolve, reject;
    const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
    return { promise, resolve, reject };
}

function scanner(t) {
    const calls = [];
    const originalDocument = globalThis.document;
    const originalWindow = globalThis.window;
    globalThis.document = { createElement: () => ({ setAttribute() {} }) };
    globalThis.window = { addEventListener() {} };
    t.after(() => {
        if (originalDocument === undefined) delete globalThis.document;
        else globalThis.document = originalDocument;
        if (originalWindow === undefined) delete globalThis.window;
        else globalThis.window = originalWindow;
    });
    t.mock.method(globalThis, 'fetch', () => {
        const call = deferred();
        calls.push(call);
        return call.promise;
    });
    t.mock.method(console, 'error', () => {});
    const card = { innerHTML: 'initial', replaceChildren(child) { this.innerHTML = child.textContent; } };
    const manualInput = { value: '' };
    let submit;
    initTicketScanner({
        card, cardUrl: '/Scanner/Tickets/Card', manualInput,
        manualForm: { addEventListener: (_, callback) => { submit = callback; } },
        startButton: { addEventListener() {} }, stopButton: { addEventListener() {} },
        labels: { lookupFailed: 'Lookup failed' },
    });
    return {
        card, calls,
        lookup(value) { manualInput.value = value; submit({ preventDefault() {} }); },
    };
}

const settle = () => new Promise(resolve => setImmediate(resolve));
const success = text => ({ ok: true, text: async () => text });

for (const failure of ['http', 'network']) {
    test(`older ${failure} failure cannot replace the newest successful ticket`, async t => {
        const s = scanner(t);
        s.lookup('OLD');
        s.lookup('CURRENT');
        s.calls[1].resolve(success('CURRENT TICKET'));
        await settle();
        if (failure === 'http') s.calls[0].resolve({ ok: false });
        else s.calls[0].reject(new Error('offline'));
        await settle();
        assert.equal(s.card.innerHTML, 'CURRENT TICKET');
    });
}

test('older successful response body cannot replace a newer lookup failure', async t => {
    const s = scanner(t);
    const body = deferred();
    s.lookup('OLD');
    s.calls[0].resolve({ ok: true, text: () => body.promise });
    await settle();
    s.lookup('CURRENT');
    s.calls[1].resolve({ ok: false });
    await settle();
    assert.equal(s.card.innerHTML, 'Lookup failed');
    body.resolve('OLD TICKET');
    await settle();
    assert.equal(s.card.innerHTML, 'Lookup failed');
});

test('current failure is visible and a later success replaces it', async t => {
    const s = scanner(t);
    s.lookup('BAD');
    s.calls[0].reject(new Error('offline'));
    await settle();
    assert.equal(s.card.innerHTML, 'Lookup failed');
    s.lookup('GOOD');
    s.calls[1].resolve(success('GOOD TICKET'));
    await settle();
    assert.equal(s.card.innerHTML, 'GOOD TICKET');
});
