// Cookie consent banner
(function () {
    var banner = document.getElementById('cookieConsent');
    if (banner && !document.cookie.split(';').some(function (c) { return c.trim().startsWith('cookieConsent='); })) {
        banner.style.display = 'block';
    }

    var acceptBtn = document.getElementById('cookieAcceptBtn');
    if (acceptBtn) {
        acceptBtn.addEventListener('click', function () {
            var expires = new Date();
            expires.setFullYear(expires.getFullYear() + 1);
            document.cookie = 'cookieConsent=accepted; expires=' + expires.toUTCString() + '; path=/; SameSite=Lax';
            banner.style.display = 'none';
        });
    }
})();

// Generic confirmation handler for [data-confirm] attributes on non-form elements (links, buttons)
document.addEventListener('click', function (e) {
    var target = e.target.closest('[data-confirm]');
    if (target && target.tagName !== 'FORM' && !target.closest('form[data-confirm]')) {
        if (!confirm(target.getAttribute('data-confirm'))) {
            e.preventDefault();
            e.stopPropagation();
        }
    }
});

// Confirmation handler for forms with [data-confirm]
document.addEventListener('submit', function (e) {
    var form = e.target.closest('form[data-confirm]');
    if (form) {
        if (!confirm(form.getAttribute('data-confirm'))) {
            e.preventDefault();
            e.stopPropagation();
        }
    }
});

// Clickable table rows via [data-href]
document.addEventListener('click', function (e) {
    var row = e.target.closest('tr[data-href]');
    if (row) {
        window.location = row.getAttribute('data-href');
    }
});

// Timezone detection — send browser IANA timezone to server session (once per session)
(function () {
    try {
        var tz = Intl.DateTimeFormat().resolvedOptions().timeZone;
        if (tz && !sessionStorage.getItem('tz_sent')) {
            fetch('/api/timezone', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ timeZone: tz })
            }).then(function (r) {
                if (r.ok) sessionStorage.setItem('tz_sent', '1');
            });
        }
    } catch (_) { /* Intl not supported — fall back to server default */ }
})();

// Human profile popover (lazy-loaded on first hover)
(function () {
    var cache = {};
    document.addEventListener('mouseenter', function (e) {
        var el = e.target.closest('[data-human-popover]');
        if (!el || el._popoverInit) return;
        el._popoverInit = true;

        var userId = el.getAttribute('data-user-id');
        if (!userId) return;

        var popover = new bootstrap.Popover(el, {
            trigger: 'hover focus',
            placement: 'auto',
            html: true,
            content: '<div class="text-center p-2"><div class="spinner-border spinner-border-sm"></div></div>',
            sanitize: false
        });
        popover.show();

        if (cache[userId]) {
            popover.setContent({ '.popover-body': cache[userId] });
        } else {
            fetch('/Human/' + userId + '/Popover')
                .then(function (r) { return r.ok ? r.text() : ''; })
                .then(function (html) {
                    if (html) {
                        cache[userId] = html;
                        popover.setContent({ '.popover-body': html });
                    }
                });
        }
    }, true);
})();
