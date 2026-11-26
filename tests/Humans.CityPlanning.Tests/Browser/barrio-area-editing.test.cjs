const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const ring = [[0, 0], [4, 0], [4, 4], [0, 4], [0, 0]];
const hole = [[1, 1], [2, 1], [2, 2], [1, 2], [1, 1]];
const feature = (type, coordinates) => ({ type: 'Feature', properties: {}, geometry: { type, coordinates } });
const multi = () => feature('MultiPolygon', [[ring, hole], [ring]]);

function editor(poly, { active = true, saving = false, warnings = false } = {}) {
    const sources = {}, checks = [], segments = [];
    let areas = 0;
    const button = { disabled: true, dataset: { saving: String(saving) } };
    const state = {
        activeCampSeasonId: active ? 'A' : null, previewCampSeasonId: active ? null : 'A',
        features: [JSON.parse(JSON.stringify(poly))],
        campMap: { campPolygons: [{ campSeasonId: 'A', spaceRequirementSqm: 100, soundZone: 0 }] },
        map: { getSource: name => ({ setData: data => { sources[name] = data; } }) },
    };
    state.draw = { getAll: () => ({ features: state.features }) };
    const context = {
        appState: state, CONFIG: { AREA_LARGER: 'Larger than {0}', AREA_SMALLER: 'Smaller than {0}',
            OUTSIDE_LIMITS: 'Outside', OVERLAPS_BARRIO: 'Overlap', SOUND_ZONE_MISMATCH: 'Sound mismatch' },
        SIZE_RATIO_UPPER: 1.5, SIZE_RATIO_LOWER: 0.5,
        document: { getElementById: id => id === 'save-btn' ? button : { addEventListener() {}, classList: { toggle() {} } } },
        isOutsideZone: f => { checks.push(f); return warnings; },
        overlapsOtherCamps: f => { checks.push(f); return warnings; },
        getSoundZoneOutOfRange: f => { checks.push(f); return warnings; },
        turf: {
            area: f => { checks.push(f); areas++; return 400; },
            centroid: () => ({ type: 'Feature', geometry: { type: 'Point', coordinates: [0, 0] } }),
            lineString: coords => {
                assert.ok(coords.every(point => typeof point[0] === 'number' && typeof point[1] === 'number'));
                segments.push(coords); return { geometry: { coordinates: coords } };
            },
            length: () => 4, point: coords => coords,
            midpoint: () => ({ type: 'Feature', geometry: { type: 'Point', coordinates: [0, 0] } }),
        },
    };
    const source = readFileSync(resolve(__dirname,
        '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/edit.js'), 'utf8')
        .replace(/^import[^\n]*;\s*$/gm, '').replaceAll('export ', '');
    vm.createContext(context); vm.runInContext(source, context);
    return { state, button, sources, segments, checks, areas: () => areas,
        update: () => context.updateSaveButton(), render: () => context.onDrawRender() };
}

test('MultiPolygon editing enables Save and labels every ring in every part', () => {
    const ui = editor(multi()); ui.update();
    assert.equal(ui.button.disabled, false);
    assert.equal(ui.sources['draw-edge-labels'].features.length, 12);
    assert.equal(ui.sources['draw-label'].features[0].properties.label, '400 m²\n⚠️ Larger than 100');
});

test('polygon holes receive edge labels alongside the outer boundary', () => {
    const ui = editor(feature('Polygon', [ring, hole])); ui.update();
    assert.equal(ui.sources['draw-edge-labels'].features.length, 8);
});

test('MultiPolygon render updates follow later-part changes without recomputing unchanged frames', () => {
    const ui = editor(multi()); ui.render();
    assert.equal(ui.areas(), 1);
    ui.render(); assert.equal(ui.areas(), 1);
    ui.state.features[0].geometry.coordinates[1][0][1][0] = 5;
    ui.render(); assert.equal(ui.areas(), 2);
});

test('an incomplete MultiPolygon part cannot enable Save or retain stale labels', () => {
    const ui = editor(multi()); ui.update();
    ui.state.features[0].geometry.coordinates[1][0] = [[0, 0], [4, 0], [0, 0]];
    ui.update();
    assert.equal(ui.button.disabled, true);
    assert.equal(ui.sources['draw-label'].features.length, 0);
    assert.equal(ui.sources['draw-edge-labels'].features.length, 0);
});

test('an active MultiPolygon save keeps Save disabled while refreshing its labels', () => {
    const ui = editor(multi(), { saving: true }); ui.update();
    assert.equal(ui.button.disabled, true);
    assert.equal(ui.sources['draw-edge-labels'].features.length, 12);
});

test('a MultiPolygon history preview shows labels without enabling Save', () => {
    const ui = editor(multi(), { active: false }); ui.update();
    assert.equal(ui.button.disabled, true);
    assert.equal(ui.sources['draw-edge-labels'].features.length, 12);
});

test('area warnings evaluate the complete MultiPolygon feature', () => {
    const ui = editor(multi(), { warnings: true }); ui.update();
    assert.equal(ui.checks.length, 4);
    assert.ok(ui.checks.every(f => f === ui.state.features[0]));
    assert.equal(ui.sources['draw-warning-error'].features[0].geometry.type, 'MultiPolygon');
    assert.equal(ui.sources['draw-warning-overlap'].features[0].geometry.type, 'MultiPolygon');
});

test('ordinary polygon editing retains Save and boundary labels', () => {
    const ui = editor(feature('Polygon', [ring])); ui.update();
    assert.equal(ui.button.disabled, false);
    assert.equal(ui.sources['draw-edge-labels'].features.length, 4);
});
