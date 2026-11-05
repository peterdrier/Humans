const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const { installHumans } = require('../../browser-support/humans-js.cjs');

const source = readFileSync(resolve(__dirname, '../../../src/Sections/Humans.Notifications/Views/Notifications/Index.cshtml'), 'utf8');
const script = source.slice(source.indexOf('<script>') + 8, source.indexOf('</script>'));
const flush = () => new Promise(done => setImmediate(done));

function inbox(outcome) {
    let requests = 0, completePost;
    const errors = [], removed = [];
    const handlers = {};
    const rows = ['actionable', 'informational'].map(kind => ({
        dataset: { notificationClass: kind, notificationId: kind + '-id' },
        addEventListener() {},
        closest: () => ({ remove: () => removed.push(kind) }),
    }));
    const elements = {
        bulkToolbar: { style: {}, dataset: { error: 'Update failed' } },
        bulkCount: { dataset: { template: '{0} selected' } },
        bulkResolveBtn: { disabled: false, style: {}, addEventListener: (_, fn) => { handlers.resolve = fn; } },
        bulkDismissBtn: { disabled: false, style: {}, addEventListener: (_, fn) => { handlers.dismiss = fn; } },
    };
    vm.runInNewContext(script, installHumans({
        document: {
            getElementById: id => elements[id] ?? null,
            querySelector: () => ({ value: 'csrf' }),
            querySelectorAll: () => rows.filter(row => !removed.includes(row.dataset.notificationClass)),
        },
        FormData: class { append() {} has() { return false; } },
        showToast: message => errors.push(message),
        fetch: async (_, options) => {
            requests++;
            assert.equal(options.method, 'POST');
            if (outcome === 'redirect' && options.redirect === 'error') throw new TypeError('redirect blocked');
            if (outcome === 'offline') throw new TypeError('offline');
            if (outcome === 'deferred') return new Promise(done => { completePost = done; });
            return { ok: outcome !== '503', status: outcome === '503' ? 503 : 200 };
        },
    }));
    return {
        errors, removed, elements, requests: () => requests,
        click: async kind => { handlers[kind](); await flush(); },
        complete: async () => { completePost({ ok: true, status: 200 }); await flush(); },
    };
}

for (const action of ['resolve', 'dismiss']) {
    for (const outcome of ['redirect', '503', 'offline']) {
        test(`bulk ${action} ${outcome} keeps selected rows and reports failure`, async () => {
            const ui = inbox(outcome);
            await ui.click(action);
            assert.deepEqual(ui.removed, []);
            assert.deepEqual(ui.errors, ['Update failed']);
            assert.equal(ui.elements.bulkResolveBtn.disabled, false);
            assert.equal(ui.elements.bulkDismissBtn.disabled, false);
        });
    }
    test(`bulk ${action} success removes only the matching selection`, async () => {
        const ui = inbox('success');
        await ui.click(action);
        assert.deepEqual(ui.removed, [action === 'resolve' ? 'actionable' : 'informational']);
        assert.deepEqual(ui.errors, []);
    });
}

test('a pending bulk action blocks repeated and overlapping submissions', async () => {
    const ui = inbox('deferred');
    await ui.click('resolve');
    assert.equal(ui.elements.bulkResolveBtn.disabled, true);
    assert.equal(ui.elements.bulkDismissBtn.disabled, true);
    await ui.click('resolve');
    await ui.click('dismiss');
    assert.equal(ui.requests(), 1);
    await ui.complete();
    assert.equal(ui.elements.bulkResolveBtn.disabled, false);
    assert.equal(ui.elements.bulkDismissBtn.disabled, false);
});
