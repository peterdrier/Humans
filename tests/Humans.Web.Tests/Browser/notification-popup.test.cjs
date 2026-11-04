const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const source = readFileSync(resolve(__dirname, '../../../src/Humans.Web/wwwroot/js/site.js'), 'utf8');
const script = source.slice(source.indexOf('// Notification bell popup'), source.indexOf('// Show a Bootstrap toast'));
const flush = () => new Promise(done => setImmediate(done));

function page(outcome) {
    let bellClick, markClick, completePost, requests = 0, badgeRemoved = false;
    const errors = [];
    const mark = {
        disabled: false, focus() {},
        getAttribute: name => name === 'data-error' ? 'Update failed' : null,
        closest: () => ({ action: '/Notifications/MarkAllRead' }),
        addEventListener: (_, handler) => { markClick = handler; },
    };
    const bell = {
        focus() {}, setAttribute() {}, getAttribute: () => 'Load failed',
        querySelector: () => badgeRemoved ? null : { remove: () => { badgeRemoved = true; } },
        addEventListener: (_, handler) => { bellClick = handler; },
    };
    const popup = {
        style: {}, addEventListener() {},
        querySelectorAll: selector => selector === '[data-ajax-markallread]' || selector.startsWith('a, button') ? [mark] : [],
    };
    const elements = {
        notificationBellWrapper: { contains: () => true },
        notificationBellBtn: bell, notificationPopup: popup,
        notificationPopupContent: { innerHTML: '' },
    };
    vm.runInNewContext(script, {
        document: { getElementById: id => elements[id] ?? null, addEventListener() {} },
        FormData: class {},
        showToast: message => errors.push(message),
        fetch: async (url, options) => {
            if (url === '/Notifications/Popup') return { ok: true, text: async () => '<p>Popup</p>' };
            requests++;
            assert.equal(options.method, 'POST');
            if (outcome === 'redirect' && options.redirect === 'error') throw new TypeError('redirect blocked');
            if (outcome === 'offline') throw new TypeError('offline');
            if (outcome === 'deferred') return new Promise(done => { completePost = done; });
            return { ok: outcome !== '503', status: outcome === '503' ? 503 : 200 };
        },
    });
    return {
        mark, errors, popup,
        badgeRemoved: () => badgeRemoved, requests: () => requests,
        toggle: async () => { bellClick({ stopPropagation() {} }); await flush(); },
        markRead: async () => { markClick({ preventDefault() {} }); await flush(); },
        complete: async () => { completePost({ ok: true, status: 200 }); await flush(); },
    };
}

for (const outcome of ['redirect', '503', 'offline']) {
    test(`mark-all-read ${outcome} preserves the badge and shows an error`, async () => {
        const ui = page(outcome);
        await ui.toggle();
        await ui.markRead();
        assert.equal(ui.badgeRemoved(), false);
        assert.equal(ui.popup.style.display, 'block');
        assert.deepEqual(ui.errors, ['Update failed']);
        assert.equal(ui.mark.disabled, false);
    });
}

test('successful mark-all-read clears the badge and closes its popup', async () => {
    const ui = page('success');
    await ui.toggle();
    await ui.markRead();
    assert.equal(ui.badgeRemoved(), true);
    assert.equal(ui.popup.style.display, 'none');
    assert.deepEqual(ui.errors, []);
    assert.equal(ui.mark.disabled, false);
});

test('mark-all-read blocks repeated clicks until the request finishes', async () => {
    const ui = page('success');
    await ui.toggle();
    await Promise.all([ui.markRead(), ui.markRead()]);
    assert.equal(ui.requests(), 1);
});

test('an earlier mark-all-read response does not close a reopened popup', async () => {
    const ui = page('deferred');
    await ui.toggle();
    await ui.markRead();
    await ui.toggle();
    await ui.toggle();
    await ui.complete();
    assert.equal(ui.badgeRemoved(), true);
    assert.equal(ui.popup.style.display, 'block');
});
