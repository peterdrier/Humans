const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const root = resolve(__dirname, '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning');

function mismatch(properties, campZone, collection = true) {
    const features = properties.map(props => ({ type: 'Feature', geometry: { type: 'Polygon', coordinates: [] }, properties: props }));
    const context = {
        appState: { campMap: { limitZoneGeoJson: JSON.stringify(collection ? { type: 'FeatureCollection', features } : features[0]) } },
        CONFIG: {},
        turf: { featureCollection: features => ({ features }), intersect: collection => collection.features[1],
            area: feature => feature.properties.area ?? 100 }, console,
    };
    vm.createContext(context);
    for (const file of ['shared/sound-zone-colors.js', 'barrio-map/geometry.js']) {
        const source = readFileSync(resolve(root, file), 'utf8')
            .replace(/^import[^\n]*;\s*$/gm, '').replaceAll('export ', '');
        vm.runInContext(source, context);
    }
    return context.getSoundZoneOutOfRange({ type: 'Feature' }, campZone);
}

for (const collection of [false, true]) {
    test(`legacy sound_zone mismatch is detected in a ${collection ? 'collection' : 'single Feature'}`, () => {
        assert.equal(mismatch([{ sound_zone: 'blue' }], 4, collection), true);
    });
}

test('legacy combined zones permit every named sound zone', () => {
    assert.equal(mismatch([{ sound_zone: 'blue_green' }], 1), false);
    assert.equal(mismatch([{ sound_zone: 'blue_green' }], 4), true);
});

test('the largest overlapping region wins across both property schemas', () => {
    assert.equal(mismatch([{ sound_zone: 'blue', area: 100 }, { SoundZone: 'red', area: 5 }], 4), true);
});

test('SoundZone keeps precedence when both property names are present', () => {
    assert.equal(mismatch([{ SoundZone: 'blue', sound_zone: 'red' }], 4), true);
});

test('current SoundZone matching and mismatching behavior remains intact', () => {
    assert.equal(mismatch([{ SoundZone: 'blue' }], 0), false);
    assert.equal(mismatch([{ SoundZone: 'blue' }], 4), true);
});

test('Surprise and unassigned camps remain exempt from sound-zone warnings', () => {
    for (const campZone of [5, -1, null, undefined]) {
        assert.equal(mismatch([{ sound_zone: 'blue' }], campZone), false);
    }
});

for (const value of [42, true, [], {}]) {
    test(`non-string ${JSON.stringify(value)} sound metadata cannot break warnings`, () => {
        assert.equal(mismatch([{ SoundZone: value }], 4), false);
        assert.equal(mismatch([{ sound_zone: value }], 4), false);
    });
}

test('warnings use a valid legacy label when the current label has an unusable type', () => {
    assert.equal(mismatch([{ SoundZone: 42, sound_zone: 'blue' }], 4), true);
});
