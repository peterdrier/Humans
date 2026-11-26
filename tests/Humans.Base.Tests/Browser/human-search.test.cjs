const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { test } = require('node:test');
const vm = require('node:vm');

// Execute the actual picker script with deterministic DOM, timers and HTTP responses.
function picker() {
    class Element {
        constructor() {
            this.value = ''; this.style = {}; this.children = []; this.handlers = {}; this.attributes = {};
            this.classes = new Set();
            this.classList = { toggle: (name, enabled) => enabled ? this.classes.add(name) : this.classes.delete(name) };
        }
        setAttribute(name, value) { this.attributes[name] = value; }
        getAttribute(name) { return this.attributes[name]; }
        removeAttribute(name) { delete this.attributes[name]; }
        scrollIntoView() { this.scrolledIntoView = true; }
        set innerHTML(value) { this.children = []; }
        addEventListener(name, handler) { this.handlers[name] = handler; }
        appendChild(child) { this.children.push(child); child.parentNode = this; }
        getBoundingClientRect() { return { bottom: 0, left: 0, width: 100 }; }
        contains(target) { return target === this; }
    }
    const elements = { input: new Element(), hidden: new Element(), dropdown: new Element() };
    for (const [id, element] of Object.entries(elements)) element.id = id;
    const timers = new Map();
    const requests = [];
    let timerId = 0;
    const documentHandlers = {};
    const document = {
        body: new Element(),
        getElementById: id => elements[id],
        createElement: () => new Element(),
        addEventListener: (event, handler) => { documentHandlers[event] = handler; },
    };
    let script = readFileSync(resolve(__dirname,
        '../../../src/Humans.Base/Views/Shared/Components/HumanSearch/Default.cshtml'), 'utf8')
        .match(/<script>([\s\S]*?)<\/script>/)[1];
    for (const [name, value] of Object.entries({ inputId: 'input', hiddenId: 'hidden', dropdownId: 'dropdown' }))
        script = script.replaceAll('@' + name, value);
    for (const [name, value] of Object.entries({ excludeIdsJson: '[]', scopeJson: '""', allowEmailJson: 'false', ticketLookupUrlJson: 'null' }))
        script = script.replaceAll('@Html.Raw(' + name + ')', value);
    vm.runInNewContext(script, {
        document, console,
        window: { addEventListener() {}, removeEventListener() {} },
        setTimeout: callback => { timers.set(++timerId, callback); return timerId; },
        clearTimeout: id => timers.delete(id),
        fetch: url => new Promise(resolve => requests.push({ url, resolve })),
    });
    return {
        ...elements, requests,
        escape() { elements.input.handlers.keydown({ key: 'Escape' }); },
        tab() { elements.input.handlers.keydown({ key: 'Tab' }); },
        key(key, extra = {}) {
            let prevented = false;
            elements.input.handlers.keydown({ key, preventDefault() { prevented = true; }, ...extra });
            return prevented;
        },
        clickOutside() { documentHandlers.mousedown({ target: {} }); },
        type(value) { elements.input.value = value; elements.input.handlers.input(); },
        search() { for (const callback of timers.values()) callback(); timers.clear(); },
        async respond(index, name) {
            requests[index].resolve({ ok: true, json: async () => (Array.isArray(name) ? name : [name]).map(value => ({ userId: value, displayName: value })) });
            await new Promise(resolve => setImmediate(resolve));
        },
        displayedName() { return elements.dropdown.children[0]?.children[0]?.children[0]?.textContent; },
    };
}

test('editing a selected name clears the submitted identity immediately', () => {
    const p = picker(); p.hidden.value = 'Alice';
    p.type('Bob');
    assert.equal(p.hidden.value, '');
});

test('a slower earlier lookup cannot replace the latest results', async () => {
    const p = picker(); p.type('Alice'); p.search(); p.type('Bob'); p.search();
    await p.respond(1, 'Bob');
    assert.equal(p.displayedName(), 'Bob');
    await p.respond(0, 'Alice');
    assert.equal(p.displayedName(), 'Bob');
});

test('clearing the query prevents an outstanding lookup from reopening results', async () => {
    const p = picker(); p.type('Alice'); p.search(); p.type('');
    await p.respond(0, 'Alice');
    assert.equal(p.dropdown.style.display, 'none');
    assert.equal(p.hidden.value, '');
});

for (const dismiss of ['escape', 'clickOutside', 'tab']) {
    for (const pending of [false, true]) {
        test(`${dismiss} keeps a dismissed lookup closed (request started: ${pending})`, async () => {
            const p = picker(); p.type('Alice');
            if (pending) p.search();
            p[dismiss]();
            p.search();
            if (p.requests.length) await p.respond(0, 'Alice');
            assert.equal(p.dropdown.style.display, 'none');
            assert.equal(p.hidden.value, '');
        });
    }
}

test('arrow navigation and Enter select a result without submitting the surrounding form', async () => {
    const p = picker(); p.type('Ali'); p.search(); await p.respond(0, ['Alice', 'Alina']);
    assert.equal(p.input.getAttribute('aria-expanded'), 'true');
    assert.equal(p.key('ArrowDown'), true);
    assert.equal(p.input.getAttribute('aria-activedescendant'), p.dropdown.children[0].id);
    assert.equal(p.dropdown.children[0].getAttribute('aria-selected'), 'true');
    assert.equal(p.key('Enter'), true);
    assert.equal(p.hidden.value, 'Alice');
    assert.equal(p.input.value, 'Alice');
    assert.equal(p.dropdown.style.display, 'none');
    assert.equal(p.input.getAttribute('aria-expanded'), 'false');
    assert.equal(p.input.getAttribute('aria-activedescendant'), undefined);
});

test('arrow keys stop at list edges in both directions', async () => {
    const p = picker(); p.type('Ali'); p.search(); await p.respond(0, ['Alice', 'Alina']);
    p.key('ArrowUp');
    assert.equal(p.input.getAttribute('aria-activedescendant'), p.dropdown.children[1].id);
    p.key('ArrowDown');
    assert.equal(p.input.getAttribute('aria-activedescendant'), p.dropdown.children[1].id);
    p.key('ArrowUp'); p.key('ArrowUp');
    assert.equal(p.input.getAttribute('aria-activedescendant'), p.dropdown.children[0].id);
    p.key('ArrowDown'); p.key('Enter');
    assert.equal(p.hidden.value, 'Alina');
});

test('a new lookup clears keyboard selection until a new result is explicitly chosen', async () => {
    const p = picker(); p.type('Alice'); p.search(); await p.respond(0, 'Alice'); p.key('ArrowDown');
    p.type('Bob'); p.search(); await p.respond(1, 'Bob');
    assert.equal(p.key('Enter'), false);
    assert.equal(p.hidden.value, '');
    p.key('ArrowDown'); p.key('Enter');
    assert.equal(p.hidden.value, 'Bob');
});

test('mouse selection still submits the chosen identity and closes the list', async () => {
    const p = picker(); p.type('Ali'); p.search(); await p.respond(0, ['Alice', 'Alina']);
    p.dropdown.children[1].handlers.mousedown({ preventDefault() {} });
    assert.equal(p.hidden.value, 'Alina');
    assert.equal(p.input.value, 'Alina');
    assert.equal(p.dropdown.style.display, 'none');
});

test('caret editing keys return to text editing without choosing a result', async () => {
    for (const key of ['ArrowLeft', 'ArrowRight', 'Home', 'End']) {
        const p = picker(); p.type('Alice'); p.search(); await p.respond(0, 'Alice'); p.key('ArrowDown');
        assert.equal(p.key(key), false);
        assert.equal(p.input.getAttribute('aria-activedescendant'), undefined);
        assert.equal(p.dropdown.children[0].getAttribute('aria-selected'), 'false');
        assert.equal(p.key('Enter'), false);
        assert.equal(p.hidden.value, '');
    }
});

test('IME composition keeps Enter available for committing text', async () => {
    const p = picker(); p.type('Alice'); p.search(); await p.respond(0, 'Alice'); p.key('ArrowDown');
    assert.equal(p.key('Enter', { isComposing: true }), false);
    assert.equal(p.hidden.value, '');
    assert.equal(p.key('Enter'), true);
    assert.equal(p.hidden.value, 'Alice');
});
