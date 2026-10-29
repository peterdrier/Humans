const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

function preview(value) {
    class Element {
        constructor(tagName) { this.tagName = tagName; this.children = []; }
        set innerHTML(_) { throw new Error('URL data must not be parsed as HTML'); }
        set textContent(value) { this.children = value ? [value] : []; }
        get textContent() { return this.children.map(child => typeof child === 'string' ? child : child.textContent).join(''); }
        append(...children) { this.children.push(...children); }
        replaceChildren(...children) { this.children = children; }
    }
    const events = {};
    const input = { value, addEventListener: (event, callback) => { events[event] = callback; } };
    const feedback = new Element('div');
    const source = readFileSync(resolve(__dirname,
        '../../../src/Humans.Base/Views/Shared/_GitHubFolderPathScript.cshtml'), 'utf8');
    const context = {
        document: {
            getElementById: id => id === 'GitHubFolderPath' ? input : feedback,
            createElement: tag => new Element(tag),
        },
        setTimeout: callback => callback(),
    };
    vm.runInNewContext(source.slice(source.indexOf('>') + 1, source.lastIndexOf('</script>')), context);
    return { input, feedback, events };
}

test('a pasted GitHub URL previews the folder without stripping server validation inputs', () => {
    const url = 'https://github.com/other-owner/other-repo/tree/other-branch/legal///';
    const page = preview(url);
    page.events.paste();
    assert.equal(page.input.value, url);
    assert.equal(page.feedback.textContent, 'Extracted folder: legal/ from other-owner/other-repo (other-branch)');
});

test('URL parts containing HTML are displayed as literal text', () => {
    const url = 'https://github.com/<img src=x>/repo/tree/main/docs/<svg onload=x>';
    const page = preview(url);
    page.events.blur();
    assert.equal(page.input.value, url);
    assert.equal(page.feedback.textContent,
        'Extracted folder: docs/<svg onload=x>/ from <img src=x>/repo (main)');
    const message = page.feedback.children[0];
    assert.equal(message.tagName, 'span');
    assert.deepEqual(message.children.filter(child => typeof child !== 'string').map(child => child.tagName), ['strong']);
});

test('a relative path or non-GitHub value clears the old preview without changing the input', () => {
    const page = preview('https://github.com/owner/repo/tree/main/legal');
    page.events.blur();
    for (const value of ['Volunteer/', '', 'https://example.com/owner/repo/tree/main/legal']) {
        page.input.value = value;
        page.events.blur();
        assert.equal(page.input.value, value);
        assert.equal(page.feedback.textContent, '');
    }
});
