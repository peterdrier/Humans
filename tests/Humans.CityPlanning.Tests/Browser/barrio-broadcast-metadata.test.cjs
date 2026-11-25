const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const root = resolve(__dirname, '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map');
const polygon = JSON.stringify({ type: 'Feature', properties: {}, geometry: {
    type: 'Polygon', coordinates: [[[0, 0], [1, 0], [1, 1], [0, 0]]] } });

function client({ known = true, placed = false } = {}) {
    const handlers = {}, sources = {};
    const season = { campSeasonId: 'A', campName: 'Camp A', campSlug: 'camp-a', spaceRequirementSqm: 200, soundZone: 0 };
    const state = {
        campMap: { campPolygons: placed ? [{ ...season, geoJson: polygon, areaSqm: 200 }] : [],
            campSeasonsWithoutPolygon: known && !placed ? [season] : [] },
        map: { getSource: name => ({ setData: data => { sources[name] = data; } }) },
    };
    const connection = { on: (name, handler) => { handlers[name] = handler; }, onreconnected() {}, start: () => new Promise(() => {}) };
    const context = {
        appState: state, CONFIG: { USER_CAMP_SEASON_ID: 'A' }, SOUND_ZONE_NAMES: {},
        signalR: { HubConnectionBuilder: function () {
            this.withUrl = this.withAutomaticReconnect = () => this;
            this.build = () => connection;
        } },
        updateAddControls() {}, console,
    };
    vm.createContext(context);
    for (const file of ['../shared/camp-geometry.js', 'geometry.js', 'signalr.js']) {
        const source = readFileSync(resolve(root, file), 'utf8')
            .replace(/^import[^\n]*;\s*$/gm, '').replaceAll('export ', '');
        vm.runInContext(source, context);
    }
    const edit = readFileSync(resolve(root, 'edit.js'), 'utf8');
    vm.runInContext(edit.slice(edit.indexOf('function getCampSoundZone'), edit.indexOf('export function onCampPolygonClick')), context);
    context.initSignalR();
    return {
        state,
        update: area => handlers.CampPolygonUpdated('A', polygon, area, 0, 'Camp A'),
        properties: () => sources['camp-polygons'].features[0].properties,
        editingRequirement: () => context.getSpaceRequirementSqm('A'),
    };
}

test('first placement retains the loaded camp link', () => {
    const ui = client(); ui.update(200);
    assert.equal(ui.properties().campSlug, 'camp-a');
});

test('first placement retains space requirements for rendered size warnings', () => {
    const ui = client(); ui.update(1000);
    assert.equal(ui.properties().spaceRequirementSqm, 200);
    assert.equal(ui.properties().spaceOutOfRange, true);
});

test('first placement retains space requirements for subsequent edits', () => {
    const ui = client(); ui.update(200);
    assert.equal(ui.editingRequirement(), 200);
});

test('later broadcasts retain metadata while replacing geometry measurements', () => {
    const ui = client(); ui.update(200); ui.update(1000);
    assert.equal(ui.state.campMap.campPolygons.length, 1);
    assert.equal(ui.properties().campSlug, 'camp-a');
    assert.equal(ui.properties().spaceOutOfRange, true);
    assert.equal(ui.properties().areaSqm, 1000);
});

test('existing placements retain their loaded metadata', () => {
    const ui = client({ placed: true }); ui.update(1000);
    assert.equal(ui.properties().campSlug, 'camp-a');
    assert.equal(ui.properties().spaceRequirementSqm, 200);
    assert.equal(ui.properties().spaceOutOfRange, true);
});

test('a season absent from the snapshot still renders its broadcast placement', () => {
    const ui = client({ known: false }); ui.update(200);
    assert.equal(ui.properties().campSeasonId, 'A');
    assert.equal(ui.properties().campName, 'Camp A');
    assert.equal(ui.properties().campSlug, '');
    assert.equal(ui.properties().spaceRequirementSqm, null);
    assert.equal(ui.properties().soundZone, 0);
});
