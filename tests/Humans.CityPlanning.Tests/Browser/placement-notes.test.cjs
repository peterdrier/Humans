const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

function page() {
    function element() {
        const classes = new Set();
        return { textContent: '', value: '', disabled: false,
            classList: { add: value => classes.add(value), remove: value => classes.delete(value),
                toggle: (value, enabled) => enabled ? classes.add(value) : classes.delete(value),
                contains: value => classes.has(value) } };
    }
    const roles = Object.fromEntries(['container-name', 'existing-image-wrap', 'existing-image',
        'remove-image-wrap', 'error', 'fields', 'save-btn'].map(role => [role, element()]));
    const textarea = element();
    let submit;
    const form = { reset() { textarea.value = ''; }, querySelector: () => textarea,
        addEventListener: (_, callback) => { submit = callback; } };
    const modal = { querySelector: selector => selector === '#placementNotesForm'
        ? form : roles[selector.match(/data-role="([^"]+)"/)[1]] };
    const saved = [], requests = [];
    let hideCount = 0;
    const context = {
        CONFIG: { YEAR: 2026 },
        document: { getElementById: () => modal },
        bootstrap: { Modal: { getOrCreateInstance: () => ({ show() {}, hide() { hideCount++; } }) } },
        FormData: class { constructor() { this.notes = textarea.value; } },
        updatePlacementNotes: (id, year, data) => new Promise((resolveResponse, reject) => {
            requests.push({ id, year, data, resolveResponse, reject });
        }),
    };
    const source = readFileSync(resolve(__dirname,
        '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/placement-notes.js'), 'utf8')
        .replace(/^import[^\n]*;\s*$/gm, '').replaceAll('export function ', 'function ');
    vm.createContext(context); vm.runInContext(source, context);
    context.initPlacementNotes((id, fields) => saved.push({ id, fields }));
    return {
        roles, textarea, requests, saved, hideCount: () => hideCount,
        open: id => context.openPlacementNotes({ id, name: id, canEdit: true, placementNotes: id + ' notes' }),
        submit: () => submit({ preventDefault() {} }),
        succeed: index => requests[index].resolveResponse({ placementNotes: 'Saved notes', placementImageUrl: '/image', placementImageFileName: 'sketch.jpg' }),
    };
}

test('a save updates its submitted container and leaves a newer container form open', async () => {
    const ui = page(); ui.open('A'); const saving = ui.submit(); ui.open('B');
    ui.succeed(0); await saving;
    assert.equal(ui.requests[0].id, 'A'); assert.equal(ui.saved[0].id, 'A');
    assert.equal(ui.saved[0].fields.placementNotes, 'Saved notes');
    assert.equal(ui.saved[0].fields.placementImageUrl, '/image');
    assert.equal(ui.hideCount(), 0); assert.equal(ui.textarea.value, 'B notes');
    assert.equal(ui.roles['save-btn'].disabled, false);
});

test('reopening the same container protects the new editing session from the old save', async () => {
    const ui = page(); ui.open('A'); const saving = ui.submit(); ui.open('A'); ui.textarea.value = 'New draft';
    ui.succeed(0); await saving;
    assert.equal(ui.hideCount(), 0); assert.equal(ui.textarea.value, 'New draft');
});

test('an earlier save failure cannot appear on a different container form', async () => {
    const ui = page(); ui.open('A'); const saving = ui.submit(); ui.open('B');
    ui.requests[0].reject(new Error('A save failed')); await saving;
    assert.equal(ui.roles.error.classList.contains('d-none'), true);
    assert.equal(ui.roles.error.textContent, ''); assert.equal(ui.hideCount(), 0);
});

test('the active save updates metadata and closes its form', async () => {
    const ui = page(); ui.open('A'); const saving = ui.submit(); ui.succeed(0); await saving;
    assert.equal(ui.saved[0].id, 'A'); assert.equal(ui.hideCount(), 1);
    assert.equal(ui.roles['save-btn'].disabled, false);
});

test('the active save failure remains visible and restores the button', async () => {
    const ui = page(); ui.open('A'); const saving = ui.submit();
    ui.requests[0].reject(new Error('Save failed')); await saving;
    assert.equal(ui.roles.error.textContent, 'Save failed');
    assert.equal(ui.roles.error.classList.contains('d-none'), false);
    assert.equal(ui.roles['save-btn'].disabled, false);
});
