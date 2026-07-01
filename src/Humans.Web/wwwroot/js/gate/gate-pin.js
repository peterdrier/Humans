// Claim keypad page client (Pin.cshtml).
//
// Collect a 4-digit PIN from the shared keypad, drop it into the hidden field, and submit the
// claim form — a full-page POST to /Gate/ClaimPin. On a wrong/weak PIN the server re-renders
// this page with an error and an empty keypad (state resets naturally on reload), so there's no
// AJAX or error-merging to get wrong. The keypad itself comes from window.initGateKeypad, a
// cache-busted classic script, so this module has no import to go stale on the kiosk.
//
// Two guards apply on *first-time set* only (confirmTwice), never on verify (which stays a single
// snappy entry): (1) an "is this you?" step keeps the keypad hidden until the staffer confirms the
// name they picked — a PIN is attributed to them; (2) the PIN must be entered twice and match, so
// a mis-typed PIN can't silently lock a volunteer out (there's no self-service reset). All of this
// lives here, in the claim-only module — the shared keypad and the override panel are untouched.
export function initClaimPin(refs) {
    const { container, form, valueInput, confirmTwice, confirmEl, confirmYes, promptEl, errorEl } = refs;
    if (!container || !form || !valueInput || !window.initGateKeypad) return;

    const setPrompt = (text, isConfirmStep) => {
        if (!promptEl) return;
        promptEl.textContent = text;
        promptEl.classList.toggle('gate-pin-prompt-confirm', !!isConfirmStep);
    };
    const showError = (text) => { if (errorEl) { errorEl.textContent = text; errorEl.classList.toggle('d-none', !text); } };

    // "Is this you?" gate (set only): reveal the keypad only once the staffer confirms.
    if (confirmEl && confirmYes) {
        confirmYes.addEventListener('click', () => {
            confirmEl.classList.add('d-none');
            form.classList.remove('d-none');
        });
    }

    let submitted = false;
    let firstPin = null; // holds the first entry while we wait for the confirming re-entry (set mode)
    const pad = window.initGateKeypad(container, (pin) => {
        if (submitted) return;

        if (confirmTwice && firstPin === null) {
            firstPin = pin; // first entry captured — ask for a confirming re-entry
            setPrompt('Re-enter your PIN to confirm', true);
            showError('');
            pad.reset();
            return;
        }
        if (confirmTwice && pin !== firstPin) {
            firstPin = null; // mismatch — start the pair over from a clean "set" prompt
            setPrompt('Set a 4-digit PIN', false);
            showError("PINs didn't match — start over");
            pad.reset();
            return;
        }

        submitted = true; // one submit per page load — a double-tap can't double-post
        valueInput.value = pin;
        form.submit();
    });
}
