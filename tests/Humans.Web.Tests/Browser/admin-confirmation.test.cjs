const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

const site = readFileSync(resolve(__dirname, '../../../src/Humans.Web/wwwroot/js/site.js'), 'utf8');
const confirmationScript = site.slice(site.indexOf('// Generic confirmation handler'),
    site.indexOf('// Auto-submit forms'));
const views = [
    ['email remediation', 'Humans.Users/Views/ProfileAdmin/EmailProblems.cshtml'],
    ['legal-document archival', 'Humans.Consent/Views/AdminLegalDocuments/LegalDocuments.cshtml'],
];

function page(viewPath, accepted) {
    const view = readFileSync(resolve(__dirname, '../../../src/Sections', viewPath), 'utf8');
    const localClicks = [];
    const delegatedClicks = [];
    const prompts = [];
    let teamChange;
    let teamSubmits = 0;
    const teamSelect = { form: { submit() { teamSubmits++; } },
        addEventListener(event, handler) { teamChange = handler; } };
    const button = {
        tagName: 'BUTTON',
        getAttribute() { return 'Confirm this action?'; },
        closest(selector) { return selector === '[data-confirm]' ? this : null; },
        addEventListener(event, handler) { localClicks.push(handler); },
    };
    const context = {
        document: {
            querySelectorAll() { return [button]; },
            getElementById() { return teamSelect; },
            addEventListener(event, handler) { if (event === 'click') delegatedClicks.push(handler); },
        },
        confirm(message) { prompts.push(message); return accepted; },
    };
    vm.createContext(context);
    for (const match of view.matchAll(/<script>([\s\S]*?)<\/script>/g)) {
        vm.runInContext(match[1], context);
    }
    vm.runInContext(confirmationScript, context);
    return {
        click() {
            const event = { target: button, defaultPrevented: false, propagationStopped: false,
                preventDefault() { this.defaultPrevented = true; },
                stopPropagation() { this.propagationStopped = true; } };
            for (const handler of localClicks) handler.call(button, event);
            if (!event.propagationStopped) {
                for (const handler of delegatedClicks) handler(event);
            }
            return !event.defaultPrevented;
        },
        prompts,
        changeTeam() { teamChange.call(teamSelect); return teamSubmits; },
    };
}

for (const [name, path] of views) {
    for (const accepted of [true, false]) {
        test(`${name} confirms once and ${accepted ? 'allows' : 'cancels'} submission`, () => {
            const ui = page(path, accepted);
            assert.equal(ui.click(), accepted);
            assert.equal(ui.prompts.length, 1);
        });
    }
}

test('legal-document team selection still submits its filter', () => {
    const ui = page(views[1][1], true);
    assert.equal(ui.changeTeam(), 1);
    assert.equal(ui.prompts.length, 0);
});
