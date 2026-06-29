// Gate admissions terminal client.
//
// Flow: a keyboard-wedge scan (or manual entry) submits the barcode → /Gate/Evaluate
// returns a verdict card. If the card asks for an ID check, the agent taps
// Yes/No (or Child) → /Gate/Decision records the decision and returns the final
// card. Distinct sounds + haptics let the agent keep their eyes on the guest,
// and the Yes/No buttons arm only after a short delay to defeat autopilot taps.

export function initGate(refs) {
    const { form, input, result, override, evaluateUrl, decisionUrl, token } = refs;

    const focusInput = () => { input.value = ''; input.focus(); };
    input.focus();
    initFreshness();

    const overrideController = initOverride();
    const overrideOpen = () => override && !override.classList.contains('d-none');

    // Render the "loaded HH:mm · N min ago" indicator and redden it once the terminal
    // has been open a while — a nudge to re-open the page so its data view isn't stale.
    // A once-a-minute text update only (no network, no scan-loop cost).
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

    // Keep the scan field focused so a wedge scan always lands somewhere useful, except
    // while the agent is interacting with the Yes/No buttons or the override panel.
    document.addEventListener('click', (e) => {
        if (overrideOpen()) return;
        if (!e.target.closest('.gate-decide') && !e.target.closest('.gate-override')
            && !e.target.closest('[data-override]') && !e.target.closest('[data-child]')) {
            input.focus();
        }
    });

    form.addEventListener('submit', async (e) => {
        e.preventDefault();
        const code = input.value.trim();
        if (!code || busy || overrideOpen()) return; // ignore scans mid-override
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
        try {
            const opts = body
                ? { method: 'POST', headers: { 'RequestVerificationToken': token }, body }
                : { headers: { 'X-Requested-With': 'fetch' } };
            const resp = await fetch(url, opts);
            if (!resp.ok) { renderError(); return; }
            result.innerHTML = await resp.text();
            afterRender();
        } catch (err) {
            console.error('Gate: request failed', err);
            renderError();
        }
    }

    // Never leave the operator staring at the previous (neutral-tinted) card on a
    // failed request — show an explicit, audible error state.
    function renderError() {
        result.innerHTML =
            '<div class="gate-card gate-stop" data-kind="Stop" role="status">' +
            '<div class="gate-icon" aria-hidden="true"><i class="fa-solid fa-triangle-exclamation"></i></div>' +
            '<div class="gate-word">Error</div>' +
            '<div class="gate-reason">Connection problem — scan again</div></div>';
        signal('Stop');
    }

    function afterRender() {
        const card = result.firstElementChild;
        if (!card) return;
        const kind = card.getAttribute('data-kind');
        signal(kind);

        // Supervisor-override affordance (too-early card, or a failed-override retry card).
        const overrideBtn = card.querySelector('[data-override="early"]');
        if (overrideBtn) {
            overrideBtn.addEventListener('click', () =>
                overrideController.open('early', overrideBtn.getAttribute('data-barcode')));
        }

        const decide = card.querySelector('.gate-decide');
        if (!decide) return;

        const barcode = decide.getAttribute('data-barcode');
        const buttons = decide.querySelectorAll('.gate-btn');

        // Anti-mistap: disable for 350ms, then arm — kills the "scan, tap-tap" carry-over.
        buttons.forEach(b => { b.setAttribute('disabled', 'disabled'); });
        setTimeout(() => buttons.forEach(b => b.removeAttribute('disabled')), 350);

        decide.querySelectorAll('[data-confirm]').forEach(btn => {
            btn.addEventListener('click', () => decide.contains(btn) && submitDecision(barcode, {
                idConfirmed: btn.getAttribute('data-confirm') === 'true',
            }));
        });

        // The child-without-ID waiver is now a supervisor override too — open the panel.
        const childBtn = decide.querySelector('[data-child]');
        if (childBtn) {
            childBtn.addEventListener('click', () => overrideController.open('child', barcode));
        }
    }

    async function submitDecision(barcode, opts) {
        if (busy) return;
        busy = true;
        const body = new URLSearchParams();
        body.set('barcode', barcode);
        body.set('idConfirmed', String(opts.idConfirmed === true));
        body.set('childWithAdult', String(opts.childWithAdult === true));
        body.set('overrideEarly', String(opts.overrideEarly === true));
        if (opts.supervisorUserId) body.set('supervisorUserId', opts.supervisorUserId);
        if (opts.supervisorPin) body.set('supervisorPin', opts.supervisorPin);
        flashNeutral(result);
        try {
            await render(decisionUrl, body);
        } finally {
            busy = false;
            focusInput();
        }
    }

    // The supervisor-override panel: pick a supervisor (tap-list — the locked-down kiosk can't
    // reach free-text people search) → enter their PIN on the shared keypad → submit the decision.
    function initOverride() {
        if (!override) return { open: () => {} };

        const titleEl = override.querySelector('[data-override-title]');
        const pickStep = override.querySelector('[data-override-pick]');
        const pinStep = override.querySelector('[data-override-pin]');
        const nameEl = override.querySelector('[data-supervisor-name]');
        const keypadEl = override.querySelector('[data-gate-keypad]');
        const cancelBtn = override.querySelector('[data-override-cancel]');

        let mode = 'early';
        let barcode = null;
        let supervisorUserId = null;

        const keypad = keypadEl && window.initGateKeypad
            ? window.initGateKeypad(keypadEl, (pin) => submit(pin))
            : { reset: () => {} };

        function open(nextMode, nextBarcode) {
            mode = nextMode;
            barcode = nextBarcode;
            supervisorUserId = null;
            if (titleEl) titleEl.textContent = mode === 'child' ? 'Authorise: child without ID' : 'Supervisor override';
            if (pinStep) pinStep.classList.add('d-none');
            if (pickStep) pickStep.classList.remove('d-none');
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

        override.querySelectorAll('[data-supervisor-id]').forEach(btn => {
            btn.addEventListener('click', () => {
                supervisorUserId = btn.getAttribute('data-supervisor-id');
                if (nameEl) nameEl.textContent = btn.textContent.trim();
                if (pickStep) pickStep.classList.add('d-none');
                if (pinStep) pinStep.classList.remove('d-none');
                keypad.reset();
            });
        });

        if (cancelBtn) cancelBtn.addEventListener('click', close);

        function submit(pin) {
            if (!supervisorUserId || !barcode) return;
            const opts = { supervisorUserId, supervisorPin: pin };
            if (mode === 'child') opts.childWithAdult = true; else opts.overrideEarly = true;
            close();
            submitDecision(barcode, opts);
        }

        return { open };
    }

    // Brief neutral frame so two greens in a row still visibly "tick over" and the
    // next guest never sees the previous result.
    function flashNeutral(container) {
        const card = container.firstElementChild;
        if (card) card.classList.add('gate-neutral');
    }

    // Distinct audio + haptic per outcome so the agent needn't watch the screen.
    function signal(kind) {
        if (kind === 'Admit') { beep([660, 990], 0.18); vibrate([30]); }
        else if (kind === 'Stop') { beep([160], 0.25); vibrate([60, 40, 60]); }
        else if (kind === 'Amber') { beep([440], 0.15); vibrate([40]); }
        // A neutral tick on the ID-confirm card cues the agent that a decision is now needed.
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
