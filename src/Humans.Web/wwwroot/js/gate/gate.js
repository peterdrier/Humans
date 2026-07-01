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

    // After a result, auto-return to the Ready screen so the previous guest's name never lingers on
    // the shared kiosk (PII) and the screen is never stuck on the last scan. Durations differ by
    // outcome: an ADMIT is a glance, but a STOP/AMBER refusal gets explained to the guest, so it
    // lingers longer; the interim ID-confirm card gets a long *safety* timeout only (it shows a name
    // but the operator is mid-decision). Any tap on the card pushes the deadline back (active use
    // keeps it up); terminal cards also get a "Next ticket" button with a visible countdown.
    const readyCard = result.firstElementChild ? result.firstElementChild.cloneNode(true) : null;
    const RESET_MS = { Admit: 10000, Stop: 30000, Amber: 30000, IdConfirm: 60000 };
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
        // Terminal cards (no pending Yes/No) get a Next button + countdown. The ID-confirm card is
        // mid-decision, so instead of a Next button it shows a visible "auto-clears in Ns · tap to
        // keep" hint — a long, *non-silent* safety net so it can't vanish unannounced mid-ID-check.
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

    // A tap anywhere on the result card means "still using this" — push the deadline back. We only
    // reschedule the setTimeout and leave the countdown setInterval running (its tick reads
    // resetDeadline live and self-corrects), so rapid taps don't rebuild the interval or make the
    // visible number stutter. One delegated listener (not per-card) so it never accumulates.
    function bumpReset() {
        if (!resetTimer) return;
        resetDeadline = Date.now() + resetMs;
        clearTimeout(resetTimer);
        resetTimer = setTimeout(resetToReady, resetMs);
    }
    result.addEventListener('pointerdown', bumpReset, { passive: true });

    // The freshness line is a reload affordance on the chromeless kiosk (no browser reload button).
    const asofEl = document.querySelector('.gate-asof');
    if (asofEl) asofEl.addEventListener('click', () => location.reload());

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
        clearResetTimer(); // a new scan/decision is incoming — cancel any pending auto-return
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
        if (!decide) {
            // Terminal result (admit / stop / amber / too-early) — no decision pending, so arm the
            // per-kind auto-return + Next-ticket button. The override panel pauses this while open.
            armAutoReset(card, kind);
            return;
        }

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

        // Scanned the wrong code — bail straight back to Ready so the operator can rescan.
        const cancelScan = decide.querySelector('[data-cancel-scan]');
        if (cancelScan) cancelScan.addEventListener('click', resetToReady);

        // The ID-confirm card shows a name, so give it a long *safety* timeout (no Next button): if
        // it's abandoned (guest walks off / staffer distracted), the name doesn't linger forever.
        // Any tap — including the Yes/No buttons — pushes the deadline back during active use.
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

    // The override panel: a single confirm tap admits a too-early or child-without-ID scan. No PIN
    // and no supervisor pick — the confirm button IS the authorization (recorded against the gate
    // account server-side). It's a deliberate second tap so a stray tap on the card can't admit.
    function initOverride() {
        if (!override) return { open: () => {} };

        const titleEl = override.querySelector('[data-override-title]');
        const promptEl = override.querySelector('[data-override-prompt]');
        const cancelBtn = override.querySelector('[data-override-cancel]');
        const confirmBtn = override.querySelector('[data-override-confirm]');

        let mode = 'early';
        let barcode = null;

        function open(nextMode, nextBarcode) {
            clearResetTimer(); // hold the auto-return while the operator confirms the override
            mode = nextMode;
            barcode = nextBarcode;
            if (titleEl) titleEl.textContent = mode === 'child' ? 'Admit: child without ID' : 'Supervisor override';
            if (promptEl) promptEl.textContent = mode === 'child'
                ? 'Admit this child with the accompanying adult?'
                : 'Admit this guest before general entry?';
            override.classList.remove('d-none');
            override.setAttribute('aria-hidden', 'false');
        }

        function close() {
            override.classList.add('d-none');
            override.setAttribute('aria-hidden', 'true');
            focusInput();
        }

        // Cancel returns to the card behind the panel — re-arm its auto-return so the guest's name
        // doesn't linger. Covers the ID-confirm card too (its timer was paused when the panel opened),
        // and matches on any card carrying a verdict (never the Ready card, which has no data-kind).
        function closeAndReArm() {
            close();
            const current = result.firstElementChild;
            if (current && current.hasAttribute('data-kind')) armAutoReset(current, current.getAttribute('data-kind'));
        }

        if (cancelBtn) cancelBtn.addEventListener('click', closeAndReArm);

        if (confirmBtn) confirmBtn.addEventListener('click', () => {
            if (!barcode) return;
            const opts = mode === 'child' ? { childWithAdult: true } : { overrideEarly: true };
            close();
            submitDecision(barcode, opts);
        });

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
