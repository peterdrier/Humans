const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const script = readFileSync(resolve(__dirname,
    '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/admin-import.js'), 'utf8');

function match(camps, properties, unplaced = []) {
    const context = {
        state: { campPolygons: camps, campSeasonsWithoutPolygon: unplaced },
        features: properties.map(properties => ({ type: 'Feature', properties,
            geometry: { type: 'Polygon', coordinates: [] } })),
        document: { getElementById: () => null }, turf: { area: () => 42 }, console: { warn() {} },
    };
    vm.createContext(context); vm.runInContext(script, context);
    return vm.runInContext('matchFeatures(features, buildCampLookup(state))', context);
}

const alpha = { campSeasonId: 'A', campName: 'Shared Name', campSlug: 'alpha', areaSqm: 10 };
const beta = { campSeasonId: 'B', campName: 'Shared Name', campSlug: 'beta' };

test('duplicate names across placement states cannot select a target', () => {
    const result = match([alpha], [{ campName: 'Shared Name' }], [beta]);
    assert.equal(result.matched.length, 0);
    assert.equal(result.unrecognized.length, 1);
    assert.match(result.unrecognized[0], /ambiguous|conflicting/);
});

test('a supplied slug disambiguates duplicate camp names', () => {
    const result = match([alpha], [{ campName: 'Shared Name', campSlug: 'alpha' }], [beta]);
    assert.equal(result.matched.length, 1);
    assert.equal(result.matched[0].campSeasonId, 'A');
    assert.equal(result.matched[0].previousAreaSqm, 10);
});

for (const field of ['campName', 'campSlug']) {
    test(`${field} matches remain separate when a name equals another slug`, () => {
        const camps = [
            { campSeasonId: 'A', campName: 'collision', campSlug: 'alpha' },
            { campSeasonId: 'B', campName: 'Beta', campSlug: 'collision' },
        ];
        const result = match(camps, [{ [field]: 'COLLISION' }]);
        assert.equal(result.matched.length, 1);
        assert.equal(result.matched[0].campSeasonId, field === 'campName' ? 'A' : 'B');
    });
}

test('conflicting identifiers are refused', () => {
    const result = match([
        { ...alpha, campName: 'Alpha' }, { ...beta, campName: 'Beta' },
    ], [{ campName: 'Alpha', campSlug: 'beta' }]);
    assert.equal(result.matched.length, 0);
    assert.match(result.unrecognized[0], /ambiguous|conflicting/);
});

test('duplicate slugs cannot select the last loaded camp', () => {
    const result = match([alpha, { ...beta, campSlug: 'alpha' }], [{ campSlug: 'alpha' }]);
    assert.equal(result.matched.length, 0);
    assert.equal(result.unrecognized.length, 1);
});

test('combined identifiers can resolve one common candidate', () => {
    const result = match([alpha, beta,
        { campSeasonId: 'C', campName: 'Other', campSlug: 'beta' },
    ], [{ campName: 'Shared Name', campSlug: 'beta' }]);
    assert.equal(result.matched.length, 1);
    assert.equal(result.matched[0].campSeasonId, 'B');
});

test('missing identifiers cannot match an empty slug', () => {
    const result = match([{ ...alpha, campSlug: '' }], [{}]);
    assert.equal(result.matched.length, 0);
    assert.equal(result.unrecognized.length, 1);
});

test('an unmatched old name still falls back to the supplied slug', () => {
    const result = match([alpha], [{ campName: 'Old Name', campSlug: 'ALPHA' }]);
    assert.equal(result.matched[0].campSeasonId, 'A');
});

test('an unmatched old slug still falls back to a unique name', () => {
    const result = match([], [{ campName: 'SHARED NAME', campSlug: 'old-slug' }], [beta]);
    assert.equal(result.matched[0].campSeasonId, 'B');
    assert.equal(result.matched[0].previousAreaSqm, null);
});

test('repeated rows for one season do not create false ambiguity', () => {
    const result = match([alpha, alpha], [{ campName: 'Shared Name' }]);
    assert.equal(result.matched.length, 1);
    assert.equal(result.matched[0].campSeasonId, 'A');
});

test('duplicate features still select the first polygon once', () => {
    const result = match([alpha], [{ campSlug: 'alpha' }, { campName: 'Shared Name' }]);
    assert.equal(result.matched.length, 1);
    assert.equal(result.unrecognized.length, 0);
});
