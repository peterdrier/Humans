const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const main = readFileSync(resolve(__dirname,
    '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/main.js'), 'utf8');
const shortcut = main.slice(main.indexOf("document.addEventListener('keydown'"),
    main.indexOf("document.getElementById('add-my-barrio-btn')"));

function editor({ active = true, vertices = 5 } = {}) {
    let keydown, deletions = 0, prevented = 0;
    const context = {
        appState: {
            activeCampSeasonId: active ? 'camp-A' : null,
            draw: {
                getAll: () => ({ features: [{ geometry: { type: 'Polygon', coordinates: [Array(vertices)] } }] }),
                trash: () => deletions++,
            },
        },
        isMeasuring: () => false,
        document: { addEventListener: (event, callback) => { assert.equal(event, 'keydown'); keydown = callback; } },
    };
    vm.createContext(context); vm.runInContext(shortcut, context);
    return {
        press(key, tagName = 'CANVAS', isContentEditable = false) {
            const target = {
                isContentEditable,
                closest: selector => selector.split(',').some(tag => tag.trim().toUpperCase() === tagName) ? target : null,
            };
            keydown({ key, target, preventDefault: () => prevented++ });
        },
        deletions: () => deletions,
        prevented: () => prevented,
    };
}

for (const key of ['Delete', 'Backspace']) {
    for (const tag of ['INPUT', 'TEXTAREA', 'SELECT', 'editable descendant']) {
        test(`${key} in ${tag} leaves the polygon and native editing untouched`, () => {
            const ui = editor();
            ui.press(key, tag === 'editable descendant' ? 'SPAN' : tag, tag === 'editable descendant');
            assert.equal(ui.deletions(), 0);
            assert.equal(ui.prevented(), 0);
        });
    }

    test(`${key} on the map retains vertex deletion`, () => {
        const ui = editor(); ui.press(key);
        assert.equal(ui.deletions(), 1);
        assert.equal(ui.prevented(), 1);
    });

    test(`${key} cannot delete a minimum polygon`, () => {
        const ui = editor({ vertices: 4 }); ui.press(key);
        assert.equal(ui.deletions(), 0);
        assert.equal(ui.prevented(), 1);
    });
}

test('Backspace without an active camp retains its native behavior', () => {
    const ui = editor({ active: false }); ui.press('Backspace');
    assert.equal(ui.deletions(), 0);
    assert.equal(ui.prevented(), 0);
});
