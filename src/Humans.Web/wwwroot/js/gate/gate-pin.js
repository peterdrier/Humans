// Claim keypad page client (Pin.cshtml).
//
// Collect a 4-digit PIN from the shared keypad, drop it into the hidden field, and submit the
// claim form — a full-page POST to /Gate/ClaimPin. On a wrong/weak PIN the server re-renders
// this page with an error and an empty keypad (state resets naturally on reload), so there's no
// AJAX or error-merging to get wrong. The keypad itself comes from window.initGateKeypad, a
// cache-busted classic script, so this module has no import to go stale on the kiosk.
export function initClaimPin(refs) {
    const { container, form, valueInput } = refs;
    if (!container || !form || !valueInput || !window.initGateKeypad) return;

    let submitted = false;
    window.initGateKeypad(container, (pin) => {
        if (submitted) return;
        submitted = true; // one submit per page load — a double-tap can't double-post
        valueInput.value = pin;
        form.submit();
    });
}
