const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

function historyPanel() {
    const events = {}, requests = [], previews = [];
    let shows = 0, deletions = 0;
    const panel = { addEventListener: (event, callback) => { (events[event] ||= []).push(callback); } };
    const list = { innerHTML: '', querySelectorAll(selector) {
        if (selector !== '.preview-btn') return [];
        return [...this.innerHTML.matchAll(/data-id="([^"]+)" data-geojson="([^"]+)"/g)].map(match => ({
            dataset: { id: match[1], geojson: match[2] }, addEventListener: (_, callback) => previews.push(callback),
        }));
    } };
    const title = { textContent: '' };
    const state = { campMap: { campPolygons: [ { campSeasonId: 'A', campName: 'Barrio A' }, { campSeasonId: 'B', campName: 'Barrio B' } ] },
        activeCampSeasonId: null, draw: { deleteAll: () => deletions++, add() {} } };
    const context = {
        appState: state, CONFIG: { HISTORY_FOR_CAMP: 'History: {0}', HISTORY_EMPTY: 'Empty', HISTORY_LOAD_FAILED: 'Load failed', PREVIEW: 'Preview', IS_MAP_ADMIN: true },
        console: { error() {} },
        document: { getElementById: id => ({ 'history-panel': panel, 'history-list': list, 'history-panel-title': title })[id],
            createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } }) },
        bootstrap: { Offcanvas: { getOrCreateInstance: () => ({ show: () => shows++ }) } },
        fetch: url => new Promise((resolveResponse, reject) => requests.push({ url, resolveResponse, reject })),
    };
    const source = readFileSync(resolve(__dirname,
        '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/edit.js'), 'utf8')
        .replace(/^import[^\n]*;\s*$/gm, '').replaceAll('export ', '');
    vm.createContext(context); vm.runInContext(source, context);
    return { list, title, state, previews, requests, shows: () => shows, deletions: () => deletions,
        load: id => context.loadHistory(id),
        finish(index, id) { requests[index].resolveResponse({ ok: true, json: async () => [{ id: id + '-version', geoJson: '{}', modifiedByDisplayName: id, modifiedAt: 'Now', areaSqm: 50, note: id }] }); },
        dismiss() { for (const event of ['hide.bs.offcanvas', 'hidden.bs.offcanvas']) for (const callback of events[event] || []) callback(); },
    };
}

test('late history success cannot replace the latest barrio and its restore controls', async () => {
    const ui = historyPanel(); const a = ui.load('A'); const b = ui.load('B');
    ui.finish(1, 'B'); await b; ui.finish(0, 'A'); await a;
    assert.equal(ui.title.textContent, 'History: Barrio B');
    assert.ok(ui.list.innerHTML.includes('B-version')); assert.ok(!ui.list.innerHTML.includes('A-version'));
    assert.equal(ui.shows(), 1);
});

test('late history failure cannot overwrite the latest successful list', async () => {
    const ui = historyPanel(); const a = ui.load('A'); const b = ui.load('B');
    ui.finish(1, 'B'); await b; ui.requests[0].reject(new Error('A failed')); await a;
    assert.ok(ui.list.innerHTML.includes('B-version')); assert.equal(ui.shows(), 1);
});

test('switching barrios removes old preview and restore controls while loading', async () => {
    const ui = historyPanel(); const a = ui.load('A'); ui.finish(0, 'A'); await a;
    assert.ok(ui.list.innerHTML.includes('A-version'));
    const b = ui.load('B'); assert.ok(!ui.list.innerHTML.includes('A-version'));
    ui.finish(1, 'B'); await b;
});

for (const fail of [false, true]) {
    test(`dismissing a panel prevents pending history ${fail ? 'failure' : 'success'} from reopening it`, async () => {
        const ui = historyPanel(); const a = ui.load('A'); ui.finish(0, 'A'); await a;
        const b = ui.load('B'); ui.dismiss();
        if (fail) ui.requests[1].reject(new Error('B failed')); else ui.finish(1, 'B');
        await b; assert.equal(ui.shows(), 1);
    });
}

test('the current failure shows the localized failure panel', async () => {
    const ui = historyPanel(); const a = ui.load('A'); ui.requests[0].reject(new Error('Failed')); await a;
    assert.ok(ui.list.innerHTML.includes('Load failed')); assert.equal(ui.shows(), 1);
});

test('dismissal clears a preview once and preserves an active edit', async () => {
    const ui = historyPanel(); const a = ui.load('A'); ui.finish(0, 'A'); await a;
    const b = ui.load('B'); ui.finish(1, 'B'); await b;
    ui.state.previewCampSeasonId = 'B'; ui.dismiss();
    assert.equal(ui.state.previewCampSeasonId, null); assert.equal(ui.deletions(), 1);
    ui.state.activeCampSeasonId = 'A'; ui.dismiss(); assert.equal(ui.deletions(), 1);
});

test('a response still decoding JSON becomes obsolete when another barrio is requested', async () => {
    const ui = historyPanel(); const a = ui.load('A');
    let finishJson;
    ui.requests[0].resolveResponse({ ok: true, json: () => new Promise(resolveJson => { finishJson = resolveJson; }) });
    await Promise.resolve();
    const b = ui.load('B'); ui.finish(1, 'B'); await b; finishJson([]); await a;
    assert.ok(ui.list.innerHTML.includes('B-version')); assert.equal(ui.shows(), 1);
});

test('obsolete success cannot replace the latest failure message', async () => {
    const ui = historyPanel(); const a = ui.load('A'); const b = ui.load('B');
    ui.requests[1].reject(new Error('B failed')); await b; ui.finish(0, 'A'); await a;
    assert.ok(ui.list.innerHTML.includes('Load failed')); assert.equal(ui.shows(), 1);
});
