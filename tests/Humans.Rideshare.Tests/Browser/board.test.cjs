const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const source = readFileSync(resolve(__dirname, '../../../src/Sections/Humans.Rideshare/wwwroot/js/rideshare/board.js'), 'utf8');

for (const mode of ['success', 'missing-library', 'map-failure', 'map-never-loads', 'fetch-failure']) {
    test(`list opens both interest forms when ${mode}`, async () => {
        const handlers = {};
        const shown = [];
        const fields = Object.fromEntries(['interestTripId', 'interestSeats', 'interestModal',
            'takeRequestId', 'takeSeats', 'takeModal'].map(id => [id, { id }]));
        const context = {
            document: {
                getElementById: id => id === 'map' ? { dataset: { boardUrl: '/api/rideshare/board' } } : fields[id],
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
                        addControl() {}, addSource() {}, addLayer() {},
                        on(event, callback) { if (event === 'load' && mode !== 'map-never-loads') callback(); },
                    };
                },
                NavigationControl: function () {}, LngLatBounds: function () {},
            },
            fetch: async () => {
                if (mode === 'fetch-failure') throw new Error('offline');
                return { json: async () => ({ features: [] }) };
            },
            console: { error() {} },
        };
        if (mode === 'missing-library') delete context.maplibregl;
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
    });
}
