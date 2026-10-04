const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

function placement() {
    const handlers = {}, documentHandlers = {}, handleHandlers = {}, frames = [], saved = [];
    let displayed;
    const canvas = { style: { cursor: '' }, getBoundingClientRect: () => ({ left: 0, top: 0 }) };
    const handle = { style: {}, addEventListener: (event, callback) => { handleHandlers[event] = callback; } };
    const map = {
        on(event, layer, callback) { handlers[layer === 'containers-editable-fill' ? `${event}:editable` : event] = callback || layer; },
        getCanvas: () => canvas, project: () => ({ x: 0, y: 0 }),
        unproject: point => ({ lng: point.x, lat: point.y }), easeTo() {}, getZoom: () => 19,
    };
    const polygon = (lng, lat, rotation) => ({ properties: { center_lng: lng, center_lat: lat, rotation_degrees: rotation } });
    const context = {
        document: { getElementById: () => handle, addEventListener: (event, callback) => { documentHandlers[event] = callback; } },
        requestAnimationFrame: callback => frames.push(callback),
        buildContainerPolygon: polygon, getRotationHandleCoordsForContainer: () => [0, 0],
        rotationFromBearing: bearing => bearing - 90,
        updateActiveSource: (_, feature) => { displayed = feature; },
        turf: { point: coords => coords, bearing: (_, coords) => coords[0] },
    };
    const source = readFileSync(resolve(__dirname,
        '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/interaction.js'), 'utf8')
        .replace(/^import[^\n]*;\s*$/gm, '').replaceAll('export function ', 'function ');
    vm.createContext(context); vm.runInContext(source, context);
    context.initInteraction(map, (container, feature) => saved.push({ id: container.id, feature: JSON.parse(JSON.stringify(feature)) }));
    const container = id => ({ id, name: id, locationGeoJson: JSON.stringify(polygon(40, 50, 20)) });
    return {
        saved, canvas, displayed: () => displayed, flush: () => { while (frames.length) frames.shift()(); },
        activate: id => context.activateContainer(container(id), 10, 20),
        select: id => context.selectPlacedContainer(container(id)), deactivate: () => context.deactivate(),
        drag() { handlers.mousedown({ lngLat: { lng: 0, lat: 0 }, preventDefault() {} }); handlers.mousemove({ lngLat: { lng: 3, lat: 4 } }); },
        rotate() { handleHandlers.mousedown({ preventDefault() {}, stopPropagation() {} }); documentHandlers.mousemove({ clientX: 135, clientY: 0 }); },
        releaseDrag: () => handlers.mouseup(), releaseRotation: () => documentHandlers.mouseup(),
    };
}

test('drag release saves the final movement before the animation frame', async () => {
    const ui = placement(); ui.activate('A'); ui.drag(); await ui.releaseDrag();
    assert.equal(ui.saved[0].feature.properties.center_lng, 13);
    assert.equal(ui.saved[0].feature.properties.center_lat, 24);
    ui.flush(); assert.equal(ui.displayed().properties.center_lng, 13);
});

test('rotation release saves the final bearing before the animation frame', async () => {
    const ui = placement(); ui.activate('A'); ui.rotate(); await ui.releaseRotation();
    assert.equal(ui.saved[0].feature.properties.rotation_degrees, 45);
    ui.flush(); assert.equal(ui.displayed().properties.rotation_degrees, 45);
});

for (const gesture of ['drag', 'rotate']) {
    for (const change of ['activate', 'select', 'deactivate']) {
        test(`${change} discards an earlier pending ${gesture} and its release`, async () => {
            const ui = placement(); ui.activate('A'); ui[gesture](); ui[change]('B'); ui.flush();
            await ui.releaseDrag(); await ui.releaseRotation();
            assert.equal(ui.saved.length, 0);
            if (change === 'deactivate') assert.equal(ui.displayed(), null);
            else {
                assert.equal(ui.displayed().properties.center_lng, change === 'select' ? 40 : 10);
                assert.equal(ui.displayed().properties.rotation_degrees, change === 'select' ? 20 : 0);
            }
            assert.equal(ui.canvas.style.cursor, '');
        });
    }
}

test('an already-rendered drag still saves once on release', async () => {
    const ui = placement(); ui.activate('A'); ui.drag(); ui.flush(); await ui.releaseDrag(); await ui.releaseDrag();
    assert.equal(ui.saved.length, 1); assert.equal(ui.saved[0].id, 'A');
    assert.equal(ui.saved[0].feature.properties.center_lng, 13);
});

for (const gesture of ['drag', 'rotate']) {
    test(`deactivate then activate discards a queued ${gesture}`, async () => {
        const ui = placement(); ui.activate('A'); ui[gesture](); ui.deactivate(); ui.activate('B'); ui.flush();
        await ui.releaseDrag(); await ui.releaseRotation();
        assert.equal(ui.saved.length, 0);
        assert.equal(ui.displayed().properties.center_lng, 10);
        assert.equal(ui.displayed().properties.rotation_degrees, 0);
    });
}

test('a fresh gesture can use a frame already queued by the previous selection', async () => {
    const ui = placement(); ui.activate('A'); ui.drag(); ui.select('B'); ui.drag(); ui.flush(); await ui.releaseDrag();
    assert.equal(ui.saved.length, 1); assert.equal(ui.saved[0].id, 'B');
    assert.equal(ui.saved[0].feature.properties.center_lng, 43);
    assert.equal(ui.saved[0].feature.properties.center_lat, 54);
});
