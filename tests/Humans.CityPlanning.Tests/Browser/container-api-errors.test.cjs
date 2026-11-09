const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync, readdirSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const { installHumans } = require('../../browser-support/humans-js.cjs');

function api(saveFailed, clearFailed, response) {
    const requests = [];
    const context = { document: {
        getElementById: () => ({ dataset: { year: '2026', i18nSaveFailed: saveFailed, i18nClearFailed: clearFailed } }),
        querySelector: () => ({ value: 'csrf' }),
    }, ESRI_TILES: [], MAP_BOUNDS: [], fetch: async (url, options) => { requests.push({ url, options }); return typeof response === 'function' ? response() : response; } };
    installHumans(context); vm.createContext(context);
    const root = resolve(__dirname, '../../../src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map');
    for (const file of ['config.js', 'api.js']) {
        const source = readFileSync(resolve(root, file), 'utf8').replace(/^import[^\n]*;\s*$/gm, '').replaceAll('export ', '');
        vm.runInContext(source, context);
    }
    return { requests, save: () => context.savePlacement('A', 2026, '{}'),
        notes: () => context.updatePlacementNotes('A', 2026, { set() {} }),
        clear: () => context.clearPlacement('A', 2026) };
}

const resources = resolve(__dirname, '../../../src/Sections/Humans.Containers');
function translations(...keys) {
    return readdirSync(resources).filter(file => /^ContainersResource(\.[\w-]+)?\.resx$/.test(file)).map(file => {
        const resx = readFileSync(resolve(resources, file), 'utf8');
        return [/^ContainersResource\.([\w-]+)\.resx$/.exec(file)?.[1] ?? 'en',
            ...keys.map(key => resx.match(new RegExp(`<data name="${key}"[^>]*>\\s*<value>([^<]*)</value>`))[1])];
    });
}

for (const [culture, saveFailed, clearFailed] of translations('Containers_MapSaveFailed', 'Containers_MapClearFailed')) {
    for (const operation of ['save', 'notes', 'clear']) {
        test(`${culture} ${operation} errors retain status and server detail with a translated prefix`, async () => {
            const client = api(saveFailed, clearFailed, { ok: false, status: 422, text: async () => 'Server detail' });
            const prefix = operation === 'clear' ? clearFailed : saveFailed;
            await assert.rejects(client[operation](), error => error.message === `${prefix} (422): Server detail`);
            assert.equal(client.requests[0].options.headers.RequestVerificationToken, 'csrf');
        });
    }
}

test('a response whose body cannot be read retains the status text', async () => {
    const client = api('Guardar falló', 'Borrar falló', { ok: false, status: 503, statusText: 'Unavailable', text: async () => { throw new Error('Body failed'); } });
    await assert.rejects(client.save(), error => error.message === 'Guardar falló (503): Unavailable');
});

test('successful placement and notes responses and empty clear response remain supported', async () => {
    const client = api('Guardar falló', 'Borrar falló', { ok: true, json: async () => ({ id: 'A' }) });
    assert.equal((await client.save()).id, 'A'); assert.equal((await client.notes()).id, 'A');
    assert.equal(await client.clear(), undefined);
});

for (const operation of ['save', 'notes', 'clear']) {
    test(`${operation} transport failures use the translated failure message`, async () => {
        const client = api('Guardar falló', 'Borrar falló', async () => { throw new TypeError('Failed to fetch'); });
        const expected = operation === 'clear' ? 'Borrar falló' : 'Guardar falló';
        await assert.rejects(client[operation](), error => error.message === expected);
    });
}

for (const operation of ['save', 'notes']) {
    test(`${operation} malformed successful responses use the translated failure message`, async () => {
        const client = api('Guardar falló', 'Borrar falló', {
            ok: true, json: async () => { throw new SyntaxError('Unexpected token < in JSON'); },
        });
        await assert.rejects(client[operation](), error => error.message === 'Guardar falló');
    });
}
