import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const source = readFileSync(new URL('../../../src/Sections/Humans.Agent/wwwroot/js/agent/widget.js', import.meta.url), 'utf8');
const text = 'event: text\ndata: {"textDelta":"Partial answer"}\n\n';
const proposal = 'event: propose\ndata: {"issueProposal":{"title":"Help"}}\n\n';
function final(reason = 'end_turn') {
    return `event: final\ndata: ${JSON.stringify({ finalizer: { stopReason: reason, conversationId: '11111111-1111-1111-1111-111111111111' } })}\n\n`;
}

class Element {
    dataset = {};
    children = [];
    handlers = {};
    style = {};
    value = '';
    ownText = '';
    set textContent(value) { this.ownText = value; this.children = []; }
    get textContent() { return this.ownText + this.children.map(child => child.textContent).join(''); }
    appendChild(child) { this.children.push(child); }
    addEventListener(event, handler) { this.handlers[event] = handler; }
    querySelector() { return { value: 'csrf' }; }
}

async function submit(chunks) {
    const elements = Object.fromEntries(['agentPanel', 'agentPanelClose', 'agentMessages', 'agentComposer', 'agentInput', 'agentSend'].map(id => [id, new Element()]));
    elements.agentPanel.dataset = { networkErrorText: 'Network error.', turnErrorText: 'Turn failed.', issueProposedText: 'Issue drafted.' };
    elements.agentInput.value = 'Question';
    let index = 0;
    const reader = { async read() {
        const chunk = chunks[index++];
        if (chunk instanceof Error) throw chunk;
        return chunk === undefined ? { done: true } : { done: false, value: new TextEncoder().encode(chunk) };
    } };
    vm.runInNewContext(source, {
        document: { getElementById: id => elements[id] ?? null, createElement: () => new Element() },
        fetch: async () => ({ ok: true, body: { getReader: () => reader } }),
        TextDecoder
    });
    await elements.agentComposer.handlers.submit({ preventDefault() {} });
    assert.equal(elements.agentSend.disabled, false);
    return elements.agentMessages.children[1];
}

test('transport failure retains the partial answer and adds an error note', async () => {
    const bubble = await submit([text, new Error('disconnect')]);
    assert.equal(bubble.textContent, 'Partial answerNetwork error.');
    assert.equal(bubble.children.length, 1);
});
test('transport failure retains the issue handoff', async () => {
    assert.equal((await submit([proposal, new Error('disconnect')])).textContent, 'Issue drafted.Network error.');
});
test('EOF without a finalizer marks a partial answer as interrupted', async () => {
    assert.equal((await submit([text])).textContent, 'Partial answerNetwork error.');
});
test('EOF without an answer or finalizer shows the network error', async () => {
    assert.equal((await submit([])).textContent, 'Network error.');
});
test('a finalizer protects a completed answer from a later transport failure', async () => {
    assert.equal((await submit([text, final(), new Error('disconnect')])).textContent, 'Partial answer');
});
test('the server error finalizer retains the partial answer', async () => {
    assert.equal((await submit([text, final('error')])).textContent, 'Partial answerTurn failed.');
});
test('a transport failure before the first answer shows the network error', async () => {
    assert.equal((await submit([new Error('disconnect')])).textContent, 'Network error.');
});
test('a completed stream retains the answer without an error note', async () => {
    assert.equal((await submit([text, final()])).textContent, 'Partial answer');
});
