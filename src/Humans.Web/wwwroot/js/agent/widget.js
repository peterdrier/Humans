(function () {
    // Note: the panel is opened by the parent help-menu (#helpWidgetAgentItem
    // in the merged HelpWidget). This script wires the panel's own controls
    // (close, composer, streaming).
    const panel = document.getElementById('agentPanel');
    const closeBtn = document.getElementById('agentPanelClose');
    const messagesEl = document.getElementById('agentMessages');
    const composer = document.getElementById('agentComposer');
    const input = document.getElementById('agentInput');
    const sendBtn = document.getElementById('agentSend');

    if (!panel || !composer) return;

    let currentConversationId = null;

    // Configure marked with GFM + breaks (single newlines render as <br>),
    // matching how Anthropic models tend to write.
    if (typeof marked !== 'undefined') {
        marked.setOptions({ breaks: true, gfm: true });
    }

    // Force <a> tags to safe defaults for external URLs (target=_blank,
    // rel=noopener noreferrer); same-tab + no rel for internal /-prefixed paths.
    if (typeof DOMPurify !== 'undefined') {
        DOMPurify.addHook('afterSanitizeAttributes', function (node) {
            if (node.tagName === 'A' && node.hasAttribute('href')) {
                const href = node.getAttribute('href');
                if (href && !href.startsWith('/')) {
                    node.setAttribute('target', '_blank');
                    node.setAttribute('rel', 'noopener noreferrer');
                } else {
                    node.removeAttribute('target');
                    node.removeAttribute('rel');
                }
            }
        });
    }

    // Allowed tags for sanitized markdown output (forbid img, iframe, script,
    // event handlers — DOMPurify defaults already strip those, but we are
    // explicit about the allow-list).
    const PURIFY_CONFIG = {
        ALLOWED_TAGS: [
            'p', 'br', 'hr',
            'ul', 'ol', 'li',
            'strong', 'em', 'code', 'pre', 'blockquote',
            'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
            'a',
            'table', 'thead', 'tbody', 'tr', 'th', 'td'
        ],
        ALLOWED_ATTR: ['href', 'target', 'rel']
    };

    function renderMarkdown(raw) {
        if (typeof marked === 'undefined' || typeof DOMPurify === 'undefined') {
            return null;
        }
        const html = marked.parse(raw);
        return DOMPurify.sanitize(html, PURIFY_CONFIG);
    }

    if (closeBtn) {
        closeBtn.addEventListener('click', function () { panel.style.display = 'none'; });
    }

    composer.addEventListener('submit', async function (e) {
        e.preventDefault();
        const message = input.value.trim();
        if (!message) return;
        input.value = '';
        sendBtn.disabled = true;

        appendMessage('user', message);
        const bubble = appendMessage('assistant', '');
        // Track raw accumulated markdown per assistant bubble so we can
        // re-parse + sanitize on each delta. innerHTML write below is safe
        // ONLY because DOMPurify gates it.
        bubble.dataset.rawMarkdown = '';

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
            bubble.dataset.rawMarkdown = (bubble.dataset.rawMarkdown || '') + parsed.textDelta;
            const sanitized = renderMarkdown(bubble.dataset.rawMarkdown);
            if (sanitized !== null) {
                // Safe: sanitized is the output of DOMPurify.sanitize.
                bubble.innerHTML = sanitized;
            } else {
                // Fallback if marked/DOMPurify failed to load — keep streaming as text.
                bubble.textContent = bubble.dataset.rawMarkdown;
            }
            messagesEl.scrollTop = messagesEl.scrollHeight;
        } else if (event === 'propose' && parsed.issueProposal) {
            // Agent called route_to_issue. Open the issue submission modal
            // pre-filled with the proposal. Preserve any answer the agent
            // already streamed — only fall back to the canned "I drafted
            // an issue" text when the bubble is empty (escalate-only turn).
            if (!bubble.dataset.rawMarkdown) {
                bubble.textContent = panel.dataset.issueProposedText || 'I drafted an issue for you. Please review and submit.';
            }
            messagesEl.scrollTop = messagesEl.scrollHeight;
            openIssueModalPrefilled(parsed.issueProposal);
        } else if (event === 'final' && parsed.finalizer) {
            const reason = parsed.finalizer.stopReason;
            // Final-frame placeholders are trusted strings — render as plain text.
            if (reason === 'disabled') bubble.textContent = '(The agent is currently disabled.)';
            if (reason === 'rate_limited') bubble.textContent = '(Daily limit reached — try again tomorrow.)';
            // Capture the conversation id from the first successful turn so the
            // next send continues the same conversation server-side. Bail-out
            // finalizers (disabled, rate_limited) carry an empty Guid; ignore
            // those so we don't clobber an existing in-progress conversation.
            const newId = parsed.finalizer.conversationId;
            if (newId && newId !== '00000000-0000-0000-0000-000000000000') {
                currentConversationId = newId;
            }
        }
    }

    function openIssueModalPrefilled(proposal) {
        const modalEl = document.getElementById('issuesWidgetModal');
        if (!modalEl || typeof bootstrap === 'undefined') return;

        const form = document.getElementById('issuesWidgetForm');
        if (form) {
            const titleInput = form.querySelector('input[name="Title"]');
            const categorySelect = form.querySelector('select[name="Category"]');
            const descriptionTextarea = form.querySelector('textarea[name="Description"]');
            if (titleInput) titleInput.value = proposal.title || '';
            if (categorySelect && proposal.category) {
                // Category is the IssueCategory enum string (Bug/Feature/Question).
                categorySelect.value = proposal.category;
            }
            if (descriptionTextarea) descriptionTextarea.value = proposal.description || '';
        }

        bootstrap.Modal.getOrCreateInstance(modalEl).show();
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
