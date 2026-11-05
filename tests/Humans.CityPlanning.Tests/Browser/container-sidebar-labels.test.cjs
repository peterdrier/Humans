const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync, readdirSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const { installHumans } = require('../../browser-support/humans-js.cjs');

function renderLabels(center, notes) {
    const cards = [];
    const sections = { 'sidebar-unplaced': { querySelector: () => null, appendChild() {} },
        'sidebar-placed': { querySelector: () => null, appendChild: card => cards.push(card) } };
    const context = { document: {
        getElementById: id => id === 'map' ? { dataset: {
            year: '2026', i18nClearPlacement: 'Clear', i18nConfirmClear: 'Confirm',
            i18nCenterOnContainer: center, i18nPlacementNotes: notes,
        } } : sections[id],
        createElement: () => ({ dataset: {}, querySelector: () => ({ addEventListener() {} }), addEventListener() {} }),
    }, ESRI_TILES: [], MAP_BOUNDS: [] };
    installHumans(context); vm.createContext(context);
    const root = resolve(__dirname, '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map');
    for (const file of ['config.js', 'sidebar.js']) {
        const source = readFileSync(resolve(root, file), 'utf8').replace(/^import[^\n]*;\s*$/gm, '').replaceAll('export ', '');
        vm.runInContext(source, context);
    }
    context.initSidebar(); context.setContainers([{ id: 'A', name: 'Container', locationGeoJson: '{}', canEdit: true }]);
    return cards[0].innerHTML;
}

const resources = resolve(__dirname, '../../../src/Sections/Humans.Containers');
function translations(...keys) {
    return readdirSync(resources).filter(file => /^ContainersResource(\.[\w-]+)?\.resx$/.test(file)).map(file => {
        const resx = readFileSync(resolve(resources, file), 'utf8');
        return [/^ContainersResource\.([\w-]+)\.resx$/.exec(file)?.[1] ?? 'en',
            ...keys.map(key => resx.match(new RegExp(`<data name="${key}"[^>]*>\\s*<value>([^<]*)</value>`))[1])];
    });
}

for (const [culture, center, notes] of translations('Containers_MapCenterOnContainer', 'Containers_PlacementNotes_Title')) {
    test(`${culture} sidebar buttons use translated titles and accessible names`, () => {
        const html = renderLabels(center, notes);
        assert.ok(html.includes(`title="${center}" aria-label="${center}"`));
        assert.ok(html.includes(`title="${notes}" aria-label="${notes}"`));
    });
}

test('translated labels are escaped before use in button attributes', () => {
    const html = renderLabels('Center "A" & B', 'Notes <A>');
    assert.ok(html.includes('title="Center &quot;A&quot; &amp; B" aria-label="Center &quot;A&quot; &amp; B"'));
    assert.ok(html.includes('title="Notes &lt;A&gt;" aria-label="Notes &lt;A&gt;"'));
});
