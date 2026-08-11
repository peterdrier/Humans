// Shared numeric PIN keypad for the gate terminal.
//
// Deliberately a *classic* script (not an ES module): it's loaded via a tag-helper
// <script src asp-append-version> so the URL is content-hashed and can never go stale on the
// kiosk — unlike a module `import` specifier, which the version tag-helper can't reach. Both
// the full-screen claim keypad (Pin.cshtml) and the inline supervisor-override panel
// (Index.cshtml) call window.initGateKeypad, so the keypad logic lives in exactly one place.
//
// Given a container holding [data-gate-dots] (the 4 masked dots) and [data-gate-pad] (the
// number buttons: [data-digit] for 0–9, [data-action="back"]/[data-action="clear"]), it
// collects a 4-digit PIN, masks it, and calls onComplete(pin) once the 4th digit lands.
// Returns { reset } so the caller can clear it (e.g. when reopening the panel).
window.initGateKeypad = function (container, onComplete) {
    if (!container) return { reset: function () {} };

    var dots = container.querySelectorAll('[data-gate-dots] .gate-dot');
    var pad = container.querySelector('[data-gate-pad]');
    var value = '';
    var done = false;

    function render() {
        dots.forEach(function (dot, i) { dot.classList.toggle('gate-dot-filled', i < value.length); });
    }

    function reset() { value = ''; done = false; render(); }

    if (pad) {
        pad.addEventListener('click', function (e) {
            var btn = e.target.closest('button');
            if (!btn || done) return;

            if (btn.dataset.action === 'back') { value = value.slice(0, -1); render(); return; }
            if (btn.dataset.action === 'clear') { reset(); return; }
            if (!btn.dataset.digit || value.length >= 4) return;

            value += btn.dataset.digit;
            render();
            if (value.length === 4) {
                done = true; // lock further taps until reset, so a stray tap can't queue a 5th digit
                var pin = value;
                // Let the 4th dot paint before we submit/navigate.
                setTimeout(function () { onComplete(pin); }, 120);
            }
        });
    }

    render();
    return { reset: reset };
};
