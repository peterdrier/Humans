// Typing "logout" instead of a barcode offers a confirm card that ends the kiosk session: the only logout path on the
// route-restricted gate account.

export function initGate(refs) {
    const { form, input, result, override, evaluateUrl, decisionUrl, logoutUrl, token } = refs;

    const focusInput = () => { input.value = ''; input.focus(); };
    input.focus();
    initFreshness();

    const overrideController = initOverride();
    const overrideOpen = () => override && !override.classList.contains('d-none');

    // Auto-return to Ready so the previous guest's name never lingers on the shared kiosk. A refusal lingers longer
    // than an admit (it gets explained); the ID-confirm card gets only a long safety timeout. Any tap pushes it back.
    const readyCard = result.firstElementChild ? result.firstElementChild.cloneNode(true) : null;
    const RESET_MS = { Admit: 10000, Stop: 30000, Amber: 30000, IdConfirm: 60000, LogoutConfirm: 60000 };
    let resetTimer = null, resetCountdown = null, resetDeadline = 0, resetMs = 0, resetLabel = null, resetLabelKind = 'next';

    function clearResetTimer() {
        if (resetTimer) { clearTimeout(resetTimer); resetTimer = null; }
        if (resetCountdown) { clearInterval(resetCountdown); resetCountdown = null; }
        resetLabel = null;
    }

    function resetToReady() {
        clearResetTimer();
        if (readyCard) result.replaceChildren(readyCard.cloneNode(true));
        focusInput();
    }

    function startResetTimer() {
        if (resetTimer) clearTimeout(resetTimer);
        if (resetCountdown) clearInterval(resetCountdown);
        resetDeadline = Date.now() + resetMs;
        resetTimer = setTimeout(resetToReady, resetMs);
        if (resetLabel) {
            const tick = () => {
                const left = Math.max(0, Math.ceil((resetDeadline - Date.now()) / 1000));
                resetLabel.textContent = resetLabelKind === 'hint'
                    ? `Auto-clears in ${left}s · tap to keep`
                    : `Next ticket › (${left})`;
            };
            tick();
            resetCountdown = setInterval(tick, 1000);
        }
    }

    function armAutoReset(card, kind) {
        clearResetTimer();
        resetMs = RESET_MS[kind] || 10000;
        // The ID-confirm card is mid-decision: a visible "auto-clears" hint instead of a Next button, so it can't
        // vanish unannounced.
        if (card.querySelector('.gate-decide')) {
            resetLabel = ensureIdHint(card);
            resetLabelKind = 'hint';
        } else {
            resetLabel = ensureNextButton(card);
            resetLabelKind = 'next';
        }
        startResetTimer();
    }

    function ensureNextButton(card) {
        let btn = card.querySelector('.gate-next');
        if (!btn) {
            btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'gate-next';
            btn.addEventListener('click', resetToReady);
            card.appendChild(btn);
        }
        return btn;
    }

    function ensureIdHint(card) {
        let hint = card.querySelector('.gate-idhint');
        if (!hint) {
            hint = document.createElement('div');
            hint.className = 'gate-idhint';
            card.appendChild(hint);
        }
        return hint;
    }

    // Only reschedule the timeout; the countdown interval reads resetDeadline live, so rapid taps don't stutter it.
    function bumpReset() {
        if (!resetTimer) return;
        resetDeadline = Date.now() + resetMs;
        clearTimeout(resetTimer);
        resetTimer = setTimeout(resetToReady, resetMs);
    }
    result.addEventListener('pointerdown', bumpReset, { passive: true });

    // A reload affordance on the chromeless kiosk, which has no reload button.
    const asofEl = document.querySelector('.gate-asof');
    if (asofEl) asofEl.addEventListener('click', () => location.reload());

    // Reddens once the terminal has been open a while: a nudge to reload.
    function initFreshness() {
        const el = document.querySelector('.gate-asof');
        const t0 = el ? Date.parse(el.getAttribute('data-asof')) : NaN;
        if (!el || isNaN(t0)) return;
        const STALE_MS = 15 * 60 * 1000;
        const hhmm = new Intl.DateTimeFormat([], { hour: '2-digit', minute: '2-digit' });
        const tick = () => {
            const ageMin = Math.max(0, Math.round((Date.now() - t0) / 60000));
            el.textContent = `loaded ${hhmm.format(t0)} · ${ageMin < 1 ? 'just now' : ageMin + ' min ago'}`;
            el.classList.toggle('gate-asof-stale', Date.now() - t0 > STALE_MS);
        };
        tick();
        setInterval(tick, 60000);
    }

    // One request at a time: a stuck wedge emitting a double Enter, or a fast
    // re-scan, must not fire two overlapping lookups whose later response wins.
    let busy = false;

    // Keep the scan field focused, except while the agent uses the buttons or the override panel.
    document.addEventListener('click', (e) => {
        if (overrideOpen()) return;
        if (!e.target.closest('.gate-decide') && !e.target.closest('.gate-override')
            && !e.target.closest('[data-override]') && !e.target.closest('[data-child]')) {
            input.focus();
        }
    });

    // Don't depend on the imager's Enter suffix: submit once a plausible barcode goes quiet. Manual mode turns this
    // off; a human pauses far longer than a wedge burst.
    const SCAN_QUIET_MS = 300;
    const SCAN_MIN_LENGTH = 5;
    let scanTimer = null;
    let manualMode = false;
    const clearScanTimer = () => { if (scanTimer) { clearTimeout(scanTimer); scanTimer = null; } };
    input.addEventListener('input', () => {
        // A scan is mid-stream: push the auto-return back, or a reset mid-burst truncates the barcode.
        bumpReset();
        clearScanTimer();
        if (manualMode || input.value.trim().length < SCAN_MIN_LENGTH) return;
        scanTimer = setTimeout(() => form.requestSubmit(), SCAN_QUIET_MS);
    });

    // Blur-then-focus makes the keyboard follow the new inputmode.
    const manualToggle = document.getElementById('gate-manual-toggle');
    if (manualToggle) manualToggle.addEventListener('click', () => {
        manualMode = !manualMode;
        clearScanTimer();
        input.setAttribute('inputmode', manualMode ? 'text' : 'none');
        manualToggle.setAttribute('aria-pressed', String(manualMode));
        manualToggle.classList.toggle('gate-manual-on', manualMode);
        input.blur();
        input.focus();
    });

    form.addEventListener('submit', async (e) => {
        e.preventDefault();
        clearScanTimer();
        const code = input.value.trim();
        if (!code || busy || overrideOpen()) return; // ignore scans mid-override
        // Confirm-tap only: a barcode that encodes "logout" must not knock the terminal offline by itself.
        if (code.toLowerCase() === 'logout') {
            showLogoutConfirm();
            return;
        }
        busy = true;
        flashNeutral(result);
        try {
            await render(`${evaluateUrl}?barcode=${encodeURIComponent(code)}`, null);
        } finally {
            busy = false;
            focusInput();
        }
    });

    async function render(url, body) {
        clearResetTimer();
        try {
            const opts = body
                ? { method: 'POST', headers: { 'RequestVerificationToken': token }, body }
                : { headers: { 'X-Requested-With': 'fetch' } };
            opts.redirect = 'error';
            const resp = await fetch(url, opts);
            if (!resp.ok) { renderError(); return; }
            result.innerHTML = await resp.text();
            afterRender();
        } catch (err) {
            console.error('Gate: request failed', err);
            renderError();
        }
    }

    // Never leave the operator staring at the previous card on a failed request.
    function renderError() {
        result.innerHTML =
            '<div class="gate-card gate-stop" data-kind="Stop" role="status">' +
            '<div class="gate-icon" aria-hidden="true"><i class="fa-solid fa-triangle-exclamation"></i></div>' +
            '<div class="gate-word">Error</div>' +
            '<div class="gate-reason">Connection problem — scan again</div></div>';
        signal('Stop');
    }

    // Touches no server until confirmed, and auto-clears so an abandoned prompt can't linger.
    function showLogoutConfirm() {
        input.value = '';
        result.innerHTML =
            '<div class="gate-card gate-idconfirm" data-kind="LogoutConfirm" role="status">' +
            '<div class="gate-icon" aria-hidden="true"><i class="fa-solid fa-right-from-bracket"></i></div>' +
            '<div class="gate-word">Log out</div>' +
            '<div class="gate-reason">Sign this device out of the gate account?</div>' +
            '<div class="gate-decide"><div class="gate-yesno">' +
            '<button type="button" class="gate-btn gate-logout-cancel" data-logout-cancel>' +
            '<i class="fa-solid fa-rotate-left" aria-hidden="true"></i> Cancel</button>' +
            '<button type="button" class="gate-btn gate-no" data-logout-confirm>' +
            '<i class="fa-solid fa-right-from-bracket" aria-hidden="true"></i> Log out</button>' +
            '</div></div></div>';

        const card = result.firstElementChild;
        card.querySelector('[data-logout-cancel]').addEventListener('click', resetToReady);

        // Same anti-mistap arming as Yes/No: a wedge-scanned "logout" auto-submits, so confirm can't be in the same motion.
        const confirmBtn = card.querySelector('[data-logout-confirm]');
        confirmBtn.setAttribute('disabled', 'disabled');
        setTimeout(() => confirmBtn.removeAttribute('disabled'), 350);
        confirmBtn.addEventListener('click', () => {
            const f = document.createElement('form');
            f.method = 'POST';
            f.action = logoutUrl;
            const t = document.createElement('input');
            t.type = 'hidden';
            t.name = '__RequestVerificationToken';
            t.value = token;
            f.appendChild(t);
            document.body.appendChild(f);
            f.submit();
        });

        armAutoReset(card, 'LogoutConfirm');
    }

    function afterRender() {
        const card = result.firstElementChild;
        if (!card) return;
        const kind = card.getAttribute('data-kind');
        signal(kind);

        const overrideBtn = card.querySelector('[data-override="early"]');
        if (overrideBtn) {
            overrideBtn.addEventListener('click', () =>
                overrideController.open('early', overrideBtn.getAttribute('data-barcode')));
        }

        const decide = card.querySelector('.gate-decide');
        if (!decide) {
            // The override panel pauses this while open.
            armAutoReset(card, kind);
            return;
        }

        const barcode = decide.getAttribute('data-barcode');
        const buttons = decide.querySelectorAll('.gate-btn');

        // Anti-mistap: disabled for 350ms kills the "scan, tap-tap" carry-over.
        buttons.forEach(b => { b.setAttribute('disabled', 'disabled'); });
        setTimeout(() => buttons.forEach(b => b.removeAttribute('disabled')), 350);

        decide.querySelectorAll('[data-confirm]').forEach(btn => {
            btn.addEventListener('click', () => decide.contains(btn) && submitDecision(barcode, {
                idConfirmed: btn.getAttribute('data-confirm') === 'true',
            }));
        });

        const childBtn = decide.querySelector('[data-child]');
        if (childBtn) {
            childBtn.addEventListener('click', () => overrideController.open('child', barcode));
        }

        const cancelScan = decide.querySelector('[data-cancel-scan]');
        if (cancelScan) cancelScan.addEventListener('click', resetToReady);

        // The card shows a name, so a long safety timeout stops it lingering if abandoned.
        armAutoReset(card, kind);
    }

    async function submitDecision(barcode, opts) {
        if (busy) return;
        busy = true;
        const body = new URLSearchParams();
        body.set('barcode', barcode);
        body.set('idConfirmed', String(opts.idConfirmed === true));
        body.set('childWithAdult', String(opts.childWithAdult === true));
        body.set('overrideEarly', String(opts.overrideEarly === true));
        if (opts.supervisorPin) body.set('supervisorPin', opts.supervisorPin);
        flashNeutral(result);
        try {
            await render(decisionUrl, body);
        } finally {
            busy = false;
            focusInput();
        }
    }

    function initOverride() {
        if (!override) return { open: () => {} };

        const titleEl = override.querySelector('[data-override-title]');
        const promptEl = override.querySelector('[data-override-prompt]');
        const cancelBtn = override.querySelector('[data-override-cancel]');
        const keypadEl = override.querySelector('[data-gate-keypad]');

        let mode = 'early';
        let barcode = null;

        const keypad = keypadEl && window.initGateKeypad
            ? window.initGateKeypad(keypadEl, (pin) => submit(pin))
            : { reset: () => {} };

        function open(nextMode, nextBarcode) {
            clearResetTimer(); // hold the auto-return while the supervisor enters the PIN
            mode = nextMode;
            barcode = nextBarcode;
            if (titleEl) titleEl.textContent = mode === 'child' ? 'Admit: child without ID' : 'Supervisor override';
            if (promptEl) promptEl.textContent = mode === 'child'
                ? 'Admit this child with the accompanying adult?'
                : 'Admit this guest before general entry?';
            keypad.reset();
            override.classList.remove('d-none');
            override.setAttribute('aria-hidden', 'false');
        }

        function close() {
            override.classList.add('d-none');
            override.setAttribute('aria-hidden', 'true');
            keypad.reset();
            focusInput();
        }

        // Re-arm the card's auto-return so the guest's name doesn't linger (its timer paused when the panel opened).
        function closeAndReArm() {
            close();
            const current = result.firstElementChild;
            if (current && current.hasAttribute('data-kind')) armAutoReset(current, current.getAttribute('data-kind'));
        }

        if (cancelBtn) cancelBtn.addEventListener('click', closeAndReArm);

        function submit(pin) {
            if (!barcode) return;
            const opts = { supervisorPin: pin };
            if (mode === 'child') opts.childWithAdult = true; else opts.overrideEarly = true;
            close();
            submitDecision(barcode, opts);
        }

        return { open };
    }

    // So two greens in a row still visibly tick over.
    function flashNeutral(container) {
        const card = container.firstElementChild;
        if (card) card.classList.add('gate-neutral');
    }

    // So the agent needn't watch the screen.
    function signal(kind) {
        if (kind === 'Admit') { beep([660, 990], 0.18); vibrate([30]); }
        else if (kind === 'Stop') { beep([160], 0.25); vibrate([60, 40, 60]); }
        else if (kind === 'Amber') { beep([440], 0.15); vibrate([40]); }
        else if (kind === 'IdConfirm') { beep([1000], 0.05); vibrate([15]); }
    }

    let audioCtx = null;
    function beep(freqs, duration) {
        try {
            audioCtx = audioCtx || new (window.AudioContext || window.webkitAudioContext)();
            let start = audioCtx.currentTime;
            for (const f of freqs) {
                const osc = audioCtx.createOscillator();
                const gain = audioCtx.createGain();
                osc.frequency.value = f;
                gain.gain.setValueAtTime(0.0001, start);
                gain.gain.exponentialRampToValueAtTime(0.2, start + 0.01);
                gain.gain.exponentialRampToValueAtTime(0.0001, start + duration);
                osc.connect(gain).connect(audioCtx.destination);
                osc.start(start);
                osc.stop(start + duration);
                start += duration;
            }
        } catch (err) { /* audio not available — haptics + visuals still cover it */ }
    }

    function vibrate(pattern) {
        if (navigator.vibrate) navigator.vibrate(pattern);
    }
}
