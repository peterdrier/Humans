const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const script = readFileSync(resolve(__dirname,
    '../../../src/Sections/Humans.Rideshare/wwwroot/js/rideshare/pick-point.js'), 'utf8');

function picker(latitude = '', longitude = '') {
    const elements = {
        Latitude: { value: latitude }, Longitude: { value: longitude }, Place: { value: 'Madrid' },
        'pick-map': { dataset: { latInput: 'Latitude', lngInput: 'Longitude', placeInput: 'Place' } },
    };
    for (const field of ['Latitude', 'Longitude', 'Place']) {
        elements[field].addEventListener = (event, handler) => { elements[field][event] = handler; };
    }
    const markers = [];
    let map;
    class Map {
        constructor(options) { this.options = options; this.handlers = {}; map = this; }
        addControl() {}
        on(event, handler) { this.handlers[event] = handler; }
        easeTo(options) { this.center = options.center; }
    }
    class Marker {
        constructor() { markers.push(this); this.removed = false; }
        setLngLat(point) { this.point = Array.from(point); return this; }
        addTo() { return this; }
        remove() { this.removed = true; }
    }
    vm.runInNewContext(script, {
        document: { getElementById: id => elements[id] },
        maplibregl: { Map, Marker, NavigationControl: class {} },
    });
    return { elements, map, markers };
}

for (const [longitude, wrapped] of [[357, -3], [-181, 179], [0, 0]]) {
    test(`map selection stores wrapped longitude ${longitude} as ${wrapped}`, () => {
        const ui = picker();
        ui.map.handlers.click({ lngLat: { lng: longitude, lat: 40,
            wrap: () => ({ lng: wrapped, lat: 40 }) } });
        assert.equal(ui.elements.Longitude.value, wrapped.toFixed(5));
        assert.equal(ui.elements.Latitude.value, '40.00000');
        assert.deepEqual(ui.markers[0].point, [wrapped, 40]);
    });
}

for (const [latitude, longitude] of [['91', '0'], ['40', '181'], ['-91', '0'], ['40', '-181']]) {
    test(`invalid initial point ${latitude},${longitude} remains unpinned`, () => {
        const ui = picker(latitude, longitude);
        assert.equal(ui.markers.length, 0);
        assert.deepEqual(Array.from(ui.map.options.center), [8, 46]);
        assert.equal(ui.elements.Latitude.value, latitude);
        assert.equal(ui.elements.Longitude.value, longitude);
    });
}

test('cleared or invalid coordinates remove the pin, and valid coordinates restore it', () => {
    const ui = picker('40', '-3');
    ui.elements.Latitude.value = '';
    ui.elements.Latitude.change();
    assert.equal(ui.markers[0].removed, true);
    ui.elements.Latitude.value = '42';
    ui.elements.Latitude.change();
    assert.deepEqual(ui.markers[1].point, [-3, 42]);
    ui.elements.Longitude.value = '181';
    ui.elements.Longitude.change();
    assert.equal(ui.markers[1].removed, true);
    assert.equal(ui.elements.Longitude.value, '181');
});

test('changing the place label still clears coordinates and removes the pin', () => {
    const ui = picker('40', '-3');
    ui.elements.Place.value = 'Barcelona';
    ui.elements.Place.change();
    assert.equal(ui.elements.Latitude.value, '');
    assert.equal(ui.elements.Longitude.value, '');
    assert.equal(ui.markers[0].removed, true);
});
