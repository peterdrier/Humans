const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const source = readFileSync(resolve(__dirname, '../../../src/Humans.Web/wwwroot/js/site.js'), 'utf8');
const script = source.slice(source.indexOf('// Favourite hearts'));

function page(removing, redirected, options = {}) {
    let click, removed = 0, reloads = 0, requests = 0;
    const errors = [];
    const rowRemoval = options.rowRemoval ?? removing;
    const buttons = (options.days ?? [null]).map(day => {
        const attrs = new Map(Object.entries({
            'data-event-id': 'event-id',
            'data-favourited': String(removing),
            'data-remove-row': String(rowRemoval),
            'data-title-add': 'Add',
            'data-title-remove': 'Remove',
            'data-error': 'Save failed',
        }));
        if (day !== null) attrs.set('data-day', day);
        const button = {
            attrs, disabled: false, removed: false,
            getAttribute: name => attrs.get(name) ?? null,
            setAttribute: (name, value) => attrs.set(name, value),
            classList: { toggle() {} },
        };
        const row = { remove: () => { removed++; button.removed = true; }, closest: () => null };
        button.closest = selector => selector === 'form'
            ? { querySelector: () => ({ value: 'token' }) }
            : selector === '.list-group-item' ? row : button;
        return button;
    });
    vm.runInNewContext(script, {
        document: {
            addEventListener: (_, handler) => { click = handler; },
            querySelectorAll: selector => buttons.filter(b => !b.removed &&
                (!selector.includes('data-remove-row="true"') || rowRemoval)),
            querySelector: () => buttons.find(b => !b.removed && rowRemoval) ?? null,
        },
        window: { location: { reload: () => { reloads++; } } },
        showToast: message => errors.push(message),
        fetch: async (_, fetchOptions) => {
            if (fetchOptions.method) {
                requests++;
                assert.equal(fetchOptions.method, removing ? 'DELETE' : 'POST');
                if (redirected && fetchOptions.redirect === 'error') throw new TypeError('redirect blocked');
                return { ok: true, status: 200 };
            }
            assert.equal(fetchOptions.redirect, 'error');
            if (options.readFails) throw new TypeError('read redirect blocked');
            const remaining = options.remainingDays ?? (removing ? [] : [0]);
            return { ok: true, status: 200, json: async () => remaining.map(day => ({
                id: 'event-id', dayOffset: day,
            })) };
        },
    });
    return {
        button: buttons[0], attrs: buttons[0].attrs, buttons, errors,
        removed: () => removed,
        reloads: () => reloads,
        requests: () => requests,
        click: async (index = 0) => {
            click({ target: buttons[index] });
            await new Promise(done => setImmediate(done));
        },
    };
}

for (const removing of [false, true]) {
    test(`a redirected ${removing ? 'schedule removal' : 'favourite add'} preserves the displayed state`, async () => {
        const ui = page(removing, true);
        await ui.click();
        assert.equal(ui.attrs.get('data-favourited'), String(removing));
        assert.equal(ui.removed(), 0);
        assert.equal(ui.reloads(), 0);
        assert.deepEqual(ui.errors, ['Save failed']);
        assert.equal(ui.button.disabled, false);
    });

    test(`a successful ${removing ? 'schedule removal' : 'favourite add'} updates the displayed state`, async () => {
        const ui = page(removing, false);
        await ui.click();
        if (removing) {
            assert.equal(ui.removed(), 1);
            assert.equal(ui.reloads(), 1);
        } else {
            assert.equal(ui.attrs.get('data-favourited'), 'true');
            assert.equal(ui.attrs.get('aria-pressed'), 'true');
        }
        assert.deepEqual(ui.errors, []);
        assert.equal(ui.button.disabled, false);
    });
}

test('removing a whole-event bookmark through one occurrence clears every heart', async () => {
    const ui = page(true, false, { rowRemoval: false, days: ['0', '2'] });
    await ui.click();
    assert.deepEqual(ui.buttons.map(b => b.attrs.get('data-favourited')), ['false', 'false']);
    assert.equal(ui.reloads(), 0);
});

test('removing one day preserves another favourited day', async () => {
    const ui = page(true, false, { rowRemoval: false, days: ['0', '2'],
        remainingDays: [2] });
    await ui.click();
    assert.deepEqual(ui.buttons.map(b => b.attrs.get('data-favourited')), ['false', 'true']);
});

test('removing an occurrence clears duplicate controls for that day', async () => {
    const ui = page(true, false, { rowRemoval: false, days: ['0', '0', '2'],
        remainingDays: [2] });
    await ui.click();
    assert.deepEqual(ui.buttons.map(b => b.attrs.get('data-favourited')), ['false', 'false', 'true']);
});

test('adding a whole-event bookmark selects every occurrence heart', async () => {
    const ui = page(false, false, { days: [null, '2'], remainingDays: [0, 2] });
    await ui.click();
    assert.deepEqual(ui.buttons.map(b => b.attrs.get('data-favourited')), ['true', 'true']);
});

test('removing a whole bookmark through one day clears every schedule row', async () => {
    const ui = page(true, false, { days: ['0', '2'] });
    await ui.click();
    assert.equal(ui.removed(), 2);
    assert.equal(ui.reloads(), 1);
});

test('controls for the same event share one pending mutation', async () => {
    const ui = page(true, false, { rowRemoval: false, days: ['0', '2'] });
    await Promise.all([ui.click(0), ui.click(1)]);
    assert.equal(ui.requests(), 1);
    assert.ok(ui.buttons.every(b => !b.disabled));
});

test('remaining day bookmarks survive removal of an overlapping whole-event bookmark', async () => {
    const ui = page(true, false, { rowRemoval: false, days: ['0', '2', '4'], remainingDays: [2] });
    await ui.click();
    assert.deepEqual(ui.buttons.map(b => b.attrs.get('data-favourited')), ['false', 'true', 'false']);
});

test('a failed refresh preserves visible state and releases all controls', async () => {
    const ui = page(true, false, { rowRemoval: false, days: ['0', '2'], readFails: true });
    await ui.click();
    assert.deepEqual(ui.buttons.map(b => b.attrs.get('data-favourited')), ['true', 'true']);
    assert.deepEqual(ui.errors, ['Save failed']);
    assert.ok(ui.buttons.every(b => !b.disabled));
});

test('surviving schedule removal buttons keep their non-toggle presentation', async () => {
    const ui = page(true, false, { days: ['0', '2'], remainingDays: [2] });
    await ui.click();
    assert.equal(ui.removed(), 1);
    assert.equal(ui.buttons[1].attrs.get('aria-pressed'), undefined);
    assert.equal(ui.buttons[1].title, undefined);
});
