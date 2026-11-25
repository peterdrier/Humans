const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const root = resolve(__dirname, '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map');

function controls({ placed = [], active = null, own = null } = {}) {
    const events = {};
    const node = () => {
        const hidden = new Set();
        return { classList: { add: name => hidden.add(name), remove: name => hidden.delete(name),
            contains: name => hidden.has(name), toggle: (name, value) => value ? hidden.add(name) : hidden.delete(name) },
        addEventListener() {} };
    };
    const toolbar = node(), container = node(), ownButton = own ? node() : null;
    const select = { options: [] };
    for (const value of ['', 'A', 'B']) {
        const option = { value, remove() { select.options = select.options.filter(item => item !== option); } };
        select.options.push(option);
    }
    const elements = { 'main-toolbar': toolbar, 'add-barrio-container': container, 'add-barrio-select': select,
        'add-my-barrio-btn': ownButton, 'history-panel': node(), 'save-btn': node() };
    const state = { activeCampSeasonId: active,
        campMap: { campPolygons: placed.map(campSeasonId => ({ campSeasonId })), campSeasonsWithoutPolygon: [] },
        map: { getSource: () => ({ setData() {} }) } };
    const connection = { on: (name, handler) => { events[name] = handler; }, onreconnected() {}, start: () => new Promise(() => {}) };
    const context = {
        appState: state, CONFIG: { USER_CAMP_SEASON_ID: own, IS_PLACEMENT_OPEN: false },
        document: { getElementById: id => elements[id] ?? null },
        signalR: { HubConnectionBuilder: function () {
            this.withUrl = this.withAutomaticReconnect = () => this; this.build = () => connection;
        } },
        buildCampPolygonFeatures: rows => rows, invalidateParsedFeature() {}, console,
    };
    vm.createContext(context);
    for (const file of ['edit.js', 'signalr.js']) {
        const source = readFileSync(resolve(root, file), 'utf8')
            .replace(/^import[^\n]*;\s*$/gm, '').replaceAll('export ', '');
        vm.runInContext(source, context);
    }
    context.initSignalR();
    // The name changes with the expanded responsibility; both versions exercise the real owner.
    const refresh = () => (context.updateAddControls ?? context.updateAddMyBarrioVisibility)();
    return { state, context, refresh, toolbar, container, ownButton,
        options: () => select.options.map(option => option.value),
        update: id => events.CampPolygonUpdated(id, '{}', 10, 0, id),
        stopEditing: () => { state.activeCampSeasonId = null; context.setEditingControlsVisible(false); },
    };
}

test('initial state removes camps already mapped since the page was rendered', () => {
    const ui = controls({ placed: ['A'] }); ui.refresh();
    assert.deepEqual(ui.options(), ['', 'B']);
});

test('first placement broadcast removes that camp from the add dropdown', () => {
    const ui = controls(); ui.update('A');
    assert.deepEqual(ui.options(), ['', 'B']);
    assert.equal(ui.container.classList.contains('d-none'), false);
    assert.equal(ui.toolbar.classList.contains('d-none'), false);
});

test('placing the last unmapped camp hides empty add controls and their idle toolbar', () => {
    const ui = controls(); ui.update('A'); ui.update('B');
    assert.deepEqual(ui.options(), ['']);
    assert.equal(ui.container.classList.contains('d-none'), true);
    assert.equal(ui.toolbar.classList.contains('d-none'), true);
});

test('an active edit keeps its toolbar when the last add option disappears', () => {
    const ui = controls({ placed: ['A'], active: 'B' }); ui.refresh(); ui.update('B');
    assert.equal(ui.container.classList.contains('d-none'), true);
    assert.equal(ui.toolbar.classList.contains('d-none'), false);
});

test('exiting the edit hides a toolbar whose add dropdown is empty', () => {
    const ui = controls({ placed: ['A'], active: 'B' }); ui.refresh(); ui.update('B'); ui.stopEditing();
    assert.equal(ui.toolbar.classList.contains('d-none'), true);
});

test('the existing member add button retains its placement-phase gate', () => {
    const ui = controls({ own: 'A' }); ui.refresh();
    assert.equal(ui.ownButton.classList.contains('d-none'), true);
    ui.context.CONFIG.IS_PLACEMENT_OPEN = true; ui.refresh();
    assert.equal(ui.ownButton.classList.contains('d-none'), false);
    ui.update('A');
    assert.equal(ui.ownButton.classList.contains('d-none'), true);
});
