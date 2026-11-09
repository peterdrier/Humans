const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { test } = require('node:test');
const vm = require('node:vm');
const { installHumans } = require('../../browser-support/humans-js.cjs');

class Element {
    constructor(tag = 'div') {
        this.tag = tag;
        this.children = [];
        this.handlers = {};
        this.value = '';
        this.style = {};
        this.className = '';
    }
    addEventListener(event, handler) { this.handlers[event] = handler; }
    appendChild(child) { this.children.push(child); child.parentNode = this; }
    remove() { this.parentNode.children.splice(this.parentNode.children.indexOf(this), 1); }
    closest(selector) {
        if (selector === this.tag || (selector.startsWith('.') && this.className.split(' ').includes(selector.slice(1)))) return this;
        return this.parentNode?.closest(selector) ?? null;
    }
    querySelectorAll(selector) {
        return this.children.flatMap(child => [
            ...(child.tag === selector ? [child] : []), ...child.querySelectorAll(selector)
        ]);
    }
}

const cases = [
    ['Grants', 'Form', 'grants-add-manager', 'grants-managers', 'manager', 'ManagerUserIds'],
    ['Grants', 'Program', 'grants-add', 'grants-issue-list', 'issue', 'userIds', 'grants-issue'],
    ['Preapprovals', 'Form', 'pre-add-manager', 'pre-managers', 'manager', 'ManagerUserIds'],
    ['Preapprovals', 'Program', 'pre-add-many', 'pre-many-list', 'many', 'userIds', 'pre-many'],
];

function page([section, view, addId, listId, key, fieldName, controls], { readOnly = false } = {}) {
    const source = readFileSync(resolve(__dirname,
        `../../../src/Sections/Humans.${section}/Views/${section}Admin/${view}.cshtml`), 'utf8');
    const elements = Object.fromEntries([...source.matchAll(/\bid="([^"]+)"/g)]
        .map(match => [match[1], new Element()]));
    elements[`human-search-${key}`] = new Element('input');
    elements[`human-search-hidden-${key}`] = new Element('input');
    if (readOnly) delete elements[addId];
    const sandbox = { document: {
        getElementById: id => elements[id] ?? null,
        createElement: tag => new Element(tag),
    } };
    installHumans(sandbox);
    const run = () => vm.runInNewContext([...source.matchAll(/<script>([\s\S]*?)<\/script>/g)].at(-1)[1], sandbox);
    const list = elements[listId];
    return { elements, list, controls, run,
        add(id, label) {
            elements[`human-search-${key}`].value = label;
            elements[`human-search-hidden-${key}`].value = id;
            elements[addId].handlers.click();
        },
        remove(item) { list.handlers.click({ target: item.children.at(-1) }); },
        fieldName,
    };
}

for (const scenario of cases) {
    const [section, view] = scenario;
    test(`${section} ${view}: adds unique posted IDs, safely labels them, clears and removes picks`, () => {
        const p = page(scenario);
        p.run();
        if (p.controls) assert.equal(p.elements[`${p.controls}-submit`].disabled, true);
        p.add('', 'Unselected search text');
        assert.equal(p.list.children.length, 0);
        p.add('person-1', '<b>A & B</b>');
        assert.equal(p.list.children.length, 1);
        const first = p.list.children[0];
        assert.equal(first.children[0].textContent, '<b>A & B</b>');
        assert.equal(first.children[1].name, p.fieldName);
        assert.equal(first.children[1].value, 'person-1');
        assert.equal(first.children[1].type, 'hidden');
        assert.equal(first.children[2].type, 'button');
        assert.equal(p.elements[`human-search-${scenario[4]}`].value, '');
        assert.equal(p.elements[`human-search-hidden-${scenario[4]}`].value, '');
        p.add('person-1', 'Duplicate');
        p.add('person-2', 'Second');
        assert.equal(p.list.children.length, 2);
        if (p.controls) {
            assert.equal(Number(p.elements[`${p.controls}-count`].textContent), 2);
            assert.equal(p.elements[`${p.controls}-submit`].disabled, false);
        }
        p.remove(first);
        p.remove(p.list.children[0]);
        assert.equal(p.list.children.length, 0);
        if (p.controls) {
            assert.equal(Number(p.elements[`${p.controls}-count`].textContent), 0);
            assert.equal(p.elements[`${p.controls}-submit`].disabled, true);
        }
        p.add('person-1', 'Can be selected again');
        assert.equal(p.list.children.length, 1);
    });

    if (view === 'Form') {
        test(`${section}: existing managers can be removed and cannot be added twice`, () => {
            const p = page(scenario);
            const item = new Element('li');
            const hidden = new Element('input');
            hidden.name = 'ManagerUserIds';
            hidden.value = 'existing-manager';
            const remove = new Element('button');
            const source = readFileSync(resolve(__dirname,
                `../../../src/Sections/Humans.${section}/Views/${section}Admin/Form.cshtml`), 'utf8');
            remove.className = source.match(/<button[^>]*class="([^"]*human-selection-remove[^"]*)"/)[1];
            item.appendChild(hidden);
            item.appendChild(remove);
            p.list.appendChild(item);
            p.run();
            p.add('existing-manager', 'Already present');
            assert.equal(p.list.children.length, 1);
            p.remove(item);
            assert.equal(p.list.children.length, 0);
        });
    } else {
        test(`${section}: read-only programs without the issue form initialize safely`, () => {
            assert.doesNotThrow(() => page(scenario, { readOnly: true }).run());
        });
    }
}
