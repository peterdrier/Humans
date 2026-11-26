const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const { installHumans } = require('../../browser-support/humans-js.cjs');

const source = readFileSync(resolve(__dirname, '../../../src/Sections/Humans.Rideshare/wwwroot/js/rideshare/board.js'), 'utf8');

for (const mode of ['success', 'missing-library', 'map-failure', 'map-never-loads', 'fetch-failure', 'http-failure', 'http-empty', 'invalid-json', 'invalid-collection', 'null-collection']) {
    test(`list opens both interest forms when ${mode}`, async () => {
        const handlers = {};
        const shown = [], toasts = [], sources = [];
        let jsonReads = 0;
        const fields = Object.fromEntries(['interestTripId', 'interestSeats', 'interestModal',
            'takeRequestId', 'takeSeats', 'takeModal'].map(id => [id, { id }]));
        const context = {
            document: {
                getElementById: id => id === 'map' ? { dataset: { boardUrl: '/api/rideshare/board', mapLoadFailed: 'Localized map failure' } } : fields[id],
                querySelectorAll: selector => [{
                    dataset: { tripId: 'trip-a', seatsRemaining: '3', requestId: 'request-b', partySize: '2' },
                    addEventListener: (_, handler) => { handlers[selector] = handler; },
                }],
            },
            bootstrap: { Modal: { getOrCreateInstance: element => ({ show: () => shown.push(element.id) }) } },
            maplibregl: {
                Map: function () {
                    if (mode === 'map-failure') throw new Error('WebGL unavailable');
                    return {
                        addControl() {}, addSource(id) { sources.push(id); }, addLayer() {},
                        on(event, callback) { if (event === 'load' && mode !== 'map-never-loads') callback(); },
                    };
                },
                NavigationControl: function () {}, LngLatBounds: function () {},
            },
            fetch: async () => {
                if (mode === 'fetch-failure') throw new Error('offline');
                return { ok: !mode.startsWith('http-'), status: mode.startsWith('http-') ? 503 : 200,
                    json: async () => {
                        jsonReads++;
                        if (mode === 'invalid-json') throw new Error('Invalid JSON');
                        if (mode === 'http-failure') return { title: 'Unavailable', status: 503 };
                        if (mode === 'invalid-collection') return { type: 'FeatureCollection', features: 'invalid' };
                        if (mode === 'null-collection') return null;
                        return { type: 'FeatureCollection', features: [] };
                    } };
            },
            console: { error() {} },
            showToast: (message, type) => toasts.push([message, type]),
        };
        if (mode === 'missing-library') delete context.maplibregl;
        installHumans(context);
        vm.runInNewContext(source, context);
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(typeof handlers['.js-interest-btn'], 'function');
        assert.equal(typeof handlers['.js-take-btn'], 'function');
        handlers['.js-interest-btn']();
        handlers['.js-take-btn']();
        assert.deepEqual(shown, ['interestModal', 'takeModal']);
        assert.equal(fields.interestTripId.value, 'trip-a');
        assert.equal(fields.interestSeats.max, '3');
        assert.equal(fields.interestSeats.value, '1');
        assert.equal(fields.takeRequestId.value, 'request-b');
        assert.equal(fields.takeSeats.value, '2');
        if (mode === 'success') {
            assert.equal(toasts.length, 0);
            assert.deepEqual(sources, ['trips', 'trip-starts', 'requests']);
        } else if (mode === 'map-never-loads') {
            assert.equal(toasts.length, 0);
            assert.equal(sources.length, 0);
        } else {
            assert.deepEqual(toasts, [['Localized map failure', 'danger']]);
            assert.equal(sources.length, 0);
        }
        if (mode.startsWith('http-')) assert.equal(jsonReads, 0);
    });
}

for (const [name, expected] of [['😀 Driver', '😀'], ['e\u0301 Rider', 'E\u0301'], ['👩‍👩‍👧‍👦 Family', '👩‍👩‍👧‍👦'], ['', '?']]) {
    test(`popup avatar preserves the first grapheme of ${JSON.stringify(name)}`, () => {
        const context = { document: { querySelectorAll: () => [], getElementById: () => null } };
        installHumans(context); vm.createContext(context); vm.runInContext(source, context);
        const html = context.personHtml(name, null, 'Driver');
        assert.ok(html.includes(`>${expected}</span>`), html);
    });
}

test('popups render escaped server-localized dates instead of machine dates', () => {
    const context = { document: { querySelectorAll: () => [], getElementById: () => null } };
    installHumans(context); vm.createContext(context); vm.runInContext(source, context);
    const offer = context.tripPopupHtml({
        departureDate: '2026-07-03', departureDateLabel: 'ven. 3 juil. <local>',
    }, key => key);
    const request = context.requestPopupHtml({
        desiredDate: '2026-07-03', desiredDateLabel: 'ven. 3 juil. <local>',
    }, key => key);
    for (const html of [offer, request]) {
        assert.ok(html.includes('ven. 3 juil. &lt;local&gt;'), html);
        assert.ok(!html.includes('2026-07-03'), html);
    }
});
