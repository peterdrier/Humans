// On a wrong or weak PIN the server re-renders this page with an empty keypad, so there is no error state to merge.
// First-time set only: an "is this you?" step, then the PIN entered twice, since there's no self-service reset.
export function initClaimPin(refs) {
    const { container, form, valueInput, confirmTwice, confirmEl, confirmYes, promptEl, errorEl } = refs;
    if (!container || !form || !valueInput || !window.initGateKeypad) return;

    const setPrompt = (text, isConfirmStep) => {
        if (!promptEl) return;
        promptEl.textContent = text;
        promptEl.classList.toggle('gate-pin-prompt-confirm', !!isConfirmStep);
    };
    const showError = (text) => { if (errorEl) { errorEl.textContent = text; errorEl.classList.toggle('d-none', !text); } };

    if (confirmEl && confirmYes) {
        confirmYes.addEventListener('click', () => {
            confirmEl.classList.add('d-none');
            form.classList.remove('d-none');
        });
    }

    let submitted = false;
    let firstPin = null;
    const pad = window.initGateKeypad(container, (pin) => {
        if (submitted) return;

        if (confirmTwice && firstPin === null) {
            firstPin = pin;
            setPrompt('Re-enter your PIN to confirm', true);
            showError('');
            pad.reset();
            return;
        }
        if (confirmTwice && pin !== firstPin) {
            firstPin = null;
            setPrompt('Set a 4-digit PIN', false);
            showError("PINs didn't match — start over");
            pad.reset();
            return;
        }

        submitted = true; // a double-tap can't double-post
        valueInput.value = pin;
        form.submit();
    });
}
