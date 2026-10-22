// A classic script, not an ES module, so asp-append-version can content-hash its URL and it never goes stale on the
// kiosk. Calls onComplete(pin) on the 4th digit and returns { reset }.
window.initGateKeypad = function (container, onComplete) {
    if (!container) return { reset: function () {} };

    var dots = container.querySelectorAll('[data-gate-dots] .gate-dot');
    var pad = container.querySelector('[data-gate-pad]');
    var value = '';
    var done = false;
    var completionTimer = null;

    function render() {
        dots.forEach(function (dot, i) { dot.classList.toggle('gate-dot-filled', i < value.length); });
    }

    function reset() {
        clearTimeout(completionTimer);
        completionTimer = null;
        value = '';
        done = false;
        render();
    }

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
                completionTimer = setTimeout(function () {
                    completionTimer = null;
                    onComplete(pin);
                }, 120);
            }
        });
    }

    render();
    return { reset: reset };
};
