const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

function ruler() {
    const sources = {}, events = {}, buttons = {}, keys = {};
    const button = id => buttons[id] ??= { classList: { add() {}, remove() {}, toggle() {} }, setAttribute() {},
        addEventListener(event, handler) { this[event] = handler; } };
    const map = {
        addSource: (id, source) => { sources[id] = source.data; }, addLayer() {},
        getSource: id => ({ setData: data => { sources[id] = data; } }),
        getCanvas: () => ({ style: {} }),
        on: (event, handler) => { (events[event] ??= new Set()).add(handler); },
        off: (event, handler) => events[event]?.delete(handler),
    };
    const context = {
        document: { documentElement: {}, getElementById: button, addEventListener: (event, handler) => { keys[event] = handler; } },
        getComputedStyle: () => ({ getPropertyValue: () => '' }),
        crypto: { randomUUID: () => 'measurement' },
        turf: { point: coordinates => coordinates, distance: () => 10 },
    };
    const source = readFileSync(resolve(__dirname,
        '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/shared/measure.js'), 'utf8')
        .replaceAll('export ', '');
    vm.createContext(context); vm.runInContext(source, context);
    context.initMeasure(map); context.wireMeasureButtons();
    const emit = (event, lng, lat) => [...(events[event] ?? [])].forEach(handler => handler({ lngLat: { lng, lat } }));
    return {
        sources, enter: () => button('measure-btn').click(), clear: () => button('clear-measurements-btn').click(),
        click: (lng, lat) => emit('click', lng, lat), move: (lng, lat) => emit('mousemove', lng, lat),
        key: key => keys.keydown?.({ key }),
        active: () => context.isMeasuring(), moves: () => events.mousemove?.size ?? 0,
    };
}

function pendingRuler() {
    const ui = ruler();
    ui.enter(); ui.click(0, 0); ui.click(1, 1);
    ui.enter(); ui.click(2, 2); ui.move(3, 3);
    return ui;
}

test('clear all removes completed measurements and the pending first point', () => {
    const ui = pendingRuler(); ui.clear();
    for (const id of ['measure-points', 'measure-line', 'measure-label', 'measure-preview-line']) {
        assert.equal(ui.sources[id].features.length, 0, id);
    }
});

test('clear all leaves measure mode awaiting a fresh first point', () => {
    const ui = pendingRuler(); ui.clear(); ui.click(5, 5);
    assert.equal(ui.active(), true);
    assert.equal(ui.sources['measure-line'].features.length, 0);
    assert.deepEqual(JSON.parse(JSON.stringify(ui.sources['measure-points'].features[0].geometry.coordinates)), [5, 5]);
});

test('clear all detaches the previous preview until a new first point is placed', () => {
    const ui = pendingRuler(); ui.clear();
    assert.equal(ui.moves(), 0);
    ui.move(9, 9);
    assert.equal(ui.sources['measure-preview-line'].features.length, 0);
    ui.click(5, 5); ui.move(6, 6);
    assert.equal(ui.sources['measure-preview-line'].features.length, 1);
});

test('the next completed measurement uses only the fresh points', () => {
    const ui = pendingRuler(); ui.clear(); ui.click(5, 5); ui.click(7, 7);
    assert.equal(ui.active(), false);
    assert.deepEqual(JSON.parse(JSON.stringify(ui.sources['measure-line'].features[0].geometry.coordinates)), [[5, 5], [7, 7]]);
});

test('clear all still clears completed measurements while idle', () => {
    const ui = ruler(); ui.enter(); ui.click(0, 0); ui.click(1, 1); ui.clear();
    assert.equal(ui.active(), false);
    assert.equal(ui.sources['measure-points'].features.length, 0);
    assert.equal(ui.sources['measure-line'].features.length, 0);
});

test('a normal two-point measurement exits measure mode', () => {
    const ui = ruler(); ui.enter(); ui.click(0, 0); ui.click(1, 1);
    assert.equal(ui.active(), false);
    assert.equal(ui.sources['measure-line'].features.length, 1);
    assert.equal(ui.moves(), 0);
});

test('Escape cancels a pending ruler while retaining completed measurements', () => {
    const ui = pendingRuler(); ui.key('Escape');
    assert.equal(ui.active(), false);
    assert.equal(ui.moves(), 0);
    assert.equal(ui.sources['measure-preview-line'].features.length, 0);
    assert.equal(ui.sources['measure-line'].features.length, 1);
    assert.equal(ui.sources['measure-points'].features.length, 2);
});

test('Escape exits measure mode before the first point', () => {
    const ui = ruler(); ui.enter(); ui.key('Escape');
    assert.equal(ui.active(), false);
    assert.equal(ui.sources['measure-points'].features.length, 0);
});

test('reopening after Escape starts with a fresh first point', () => {
    const ui = pendingRuler(); ui.key('Escape'); ui.enter(); ui.click(5, 5);
    assert.equal(ui.active(), true);
    assert.equal(ui.sources['measure-line'].features.length, 1);
    assert.equal(ui.sources['measure-points'].features.length, 3);
});

test('Escape while idle leaves completed measurements untouched', () => {
    const ui = ruler(); ui.enter(); ui.click(0, 0); ui.click(1, 1);
    const lines = ui.sources['measure-line']; ui.key('Escape');
    assert.equal(ui.active(), false);
    assert.equal(ui.sources['measure-line'], lines);
});

test('other keys leave the pending ruler active', () => {
    const ui = pendingRuler(); ui.key('Enter');
    assert.equal(ui.active(), true);
    assert.equal(ui.sources['measure-preview-line'].features.length, 1);
});
