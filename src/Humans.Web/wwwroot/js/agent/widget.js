(function () {
    const launcher = document.getElementById('agentWidgetLauncher');
    const panel = document.getElementById('agentPanel');
    const closeBtn = document.getElementById('agentPanelClose');
    const messagesEl = document.getElementById('agentMessages');
    const composer = document.getElementById('agentComposer');
    const input = document.getElementById('agentInput');
    const sendBtn = document.getElementById('agentSend');

    if (!launcher || !panel) return;

    let currentConversationId = null;

    launcher.addEventListener('click', function () {
        panel.style.display = panel.style.display === 'none' ? 'block' : 'none';
    });
    closeBtn.addEventListener('click', function () { panel.style.display = 'none'; });

    composer.addEventListener('submit', async function (e) {
        e.preventDefault();
        const message = input.value.trim();
        if (!message) return;
        input.value = '';
        sendBtn.disabled = true;

        appendMessage('user', message);
        const bubble = appendMessage('assistant', '');

        try {
            const resp = await fetch('/Agent/Ask', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'Accept': 'text/event-stream',
                    'RequestVerificationToken': composer.querySelector('input[name="__RequestVerificationToken"]').value
                },
                body: JSON.stringify({ conversationId: currentConversationId, message: message })
            });
            if (!resp.ok) {
                bubble.textContent = 'Error: ' + resp.status;
                return;
            }
            const reader = resp.body.getReader();
            const decoder = new TextDecoder();
            let buf = '';
            while (true) {
                const { value, done } = await reader.read();
                if (done) break;
                buf += decoder.decode(value, { stream: true });
                let idx;
                while ((idx = buf.indexOf('\n\n')) >= 0) {
                    const frame = buf.slice(0, idx);
                    buf = buf.slice(idx + 2);
                    handleFrame(frame, bubble);
                }
            }
        } catch (err) {
            bubble.textContent = 'Network error.';
        } finally {
            sendBtn.disabled = false;
        }
    });

    function handleFrame(frame, bubble) {
        const lines = frame.split('\n');
        let event = 'message', data = '';
        for (const line of lines) {
            if (line.startsWith('event: ')) event = line.slice(7);
            else if (line.startsWith('data: ')) data = line.slice(6);
        }
        if (!data) return;
        const parsed = JSON.parse(data);
        if (event === 'text' && parsed.textDelta) {
            bubble.textContent += parsed.textDelta;
            messagesEl.scrollTop = messagesEl.scrollHeight;
        } else if (event === 'final' && parsed.finalizer) {
            const reason = parsed.finalizer.stopReason;
            if (reason === 'disabled') bubble.textContent = '(The agent is currently disabled.)';
            if (reason === 'rate_limited') bubble.textContent = '(Daily limit reached — try again tomorrow.)';
        }
    }

    function appendMessage(role, text) {
        const div = document.createElement('div');
        div.className = 'agent-msg agent-msg-' + role;
        div.textContent = text;
        messagesEl.appendChild(div);
        messagesEl.scrollTop = messagesEl.scrollHeight;
        return div;
    }
})();
