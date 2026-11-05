const assert = require('node:assert/strict');
const { test } = require('node:test');
const vm = require('node:vm');
const { installHumans, namespaceSource } = require('../../browser-support/humans-js.cjs');

function humans(sandbox = {}) {
    const calls = [];
    sandbox.fetch ??= async (url, init) => { calls.push({ url, init }); return { ok: true }; };
    return { Humans: installHumans(sandbox).Humans, calls };
}

test('escapeHtml escapes text and attribute delimiters, and maps null to empty', () => {
    const { Humans } = humans();
    assert.equal(Humans.escapeHtml(`<a href="x">Tom & 'Jerry'</a>`),
        '&lt;a href=&quot;x&quot;&gt;Tom &amp; &#39;Jerry&#39;&lt;/a&gt;');
    assert.equal(Humans.escapeHtml(null), '');
    assert.equal(Humans.escapeHtml(undefined), '');
    assert.equal(Humans.escapeHtml(0), '0');
});

test('csrfToken reads the input, then the meta tag, then falls back to empty', () => {
    // No installHumans here: it answers token lookups itself, and this test needs the real order.
    const lookups = {};
    const host = { document: { querySelector: selector => lookups[selector] ?? null } };
    host.window = host;
    vm.runInNewContext(namespaceSource, host);
    assert.equal(host.Humans.csrfToken(), '');
    lookups['meta[name="__RequestVerificationToken"]'] = { getAttribute: () => 'from-meta' };
    assert.equal(host.Humans.csrfToken(), 'from-meta');
    lookups['input[name="__RequestVerificationToken"]'] = { value: 'from-input' };
    assert.equal(host.Humans.csrfToken(), 'from-input');
    assert.equal(host.Humans.csrfToken('#scoped'), '');
});

test('postJson sends the antiforgery header and a JSON body', async () => {
    const { Humans, calls } = humans();
    await Humans.postJson('/api/x', { a: 1 });
    const { url, init } = calls[0];
    assert.equal(url, '/api/x');
    assert.equal(init.method, 'POST');
    assert.equal(init.credentials, 'same-origin');
    assert.equal(init.headers.RequestVerificationToken, 'csrf');
    assert.equal(init.headers['Content-Type'], 'application/json');
    assert.equal(init.body, '{"a":1}');
});

test('postJson honours method, extra headers and fetch options, and omits Content-Type without a body', async () => {
    const { Humans, calls } = humans();
    await Humans.postJson('/api/x', { a: 1 }, { method: 'PUT', headers: { Accept: 'text/event-stream' }, redirect: 'error' });
    assert.equal(calls[0].init.method, 'PUT');
    assert.equal(calls[0].init.redirect, 'error');
    assert.equal(calls[0].init.headers.Accept, 'text/event-stream');
    assert.equal(calls[0].init.headers.RequestVerificationToken, 'csrf');
    await Humans.postJson('/api/y');
    assert.equal(calls[1].init.body, undefined);
    assert.equal(calls[1].init.headers['Content-Type'], undefined);
});

test('postForm adds the token to the body and header for plain objects, URLSearchParams and FormData', async () => {
    const { Humans, calls } = humans();
    await Humans.postForm('/f', { name: 'Heavy lifting' });
    assert.equal(calls[0].init.body.get('name'), 'Heavy lifting');
    assert.equal(calls[0].init.body.get('__RequestVerificationToken'), 'csrf');
    assert.equal(calls[0].init.headers.RequestVerificationToken, 'csrf');

    const params = new URLSearchParams({ a: '1', __RequestVerificationToken: 'own' });
    await Humans.postForm('/f', params, { redirect: 'error' });
    assert.deepEqual(calls[1].init.body.getAll('__RequestVerificationToken'), ['own']);
    assert.equal(calls[1].init.redirect, 'error');

    const form = new FormData();
    form.append('shiftId', '7');
    await Humans.postForm('/f', form, { headers: { 'X-Requested-With': 'XMLHttpRequest' } });
    assert.equal(calls[2].init.body, form);
    assert.equal(form.get('__RequestVerificationToken'), 'csrf');
    assert.equal(calls[2].init.headers['X-Requested-With'], 'XMLHttpRequest');
});

test('loadChartJs appends one pinned, integrity-checked script and shares it across callers', async () => {
    const appended = [];
    const { Humans } = humans({ document: {
        createElement: () => ({}),
        head: { appendChild: script => appended.push(script) },
    } });
    const first = Humans.loadChartJs(), second = Humans.loadChartJs();
    assert.equal(appended.length, 1);
    assert.match(appended[0].src, /^https:\/\/cdn\.jsdelivr\.net\/npm\/chart\.js@\d+\.\d+\.\d+\/dist\/chart\.umd\.min\.js$/);
    assert.match(appended[0].integrity, /^sha384-/);
    assert.equal(appended[0].crossOrigin, 'anonymous');
    appended[0].onload();
    await Promise.all([first, second]);
});

test('loadChartJs can retry after a failed load', async () => {
    const appended = [];
    const { Humans } = humans({ document: {
        createElement: () => ({}),
        head: { appendChild: script => appended.push(script) },
    } });
    const failed = Humans.loadChartJs();
    appended[0].onerror();
    await assert.rejects(failed, /Chart\.js failed to load/);
    Humans.loadChartJs();
    assert.equal(appended.length, 2);
});
