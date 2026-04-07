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

// Auto-submit forms when a .js-auto-submit element changes
document.addEventListener('change', function (e) {
    if (e.target.closest('.js-auto-submit') && e.target.form) {
        e.target.form.submit();
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

// Notification bell popup
(function () {
    var wrapper = document.getElementById('notificationBellWrapper');
    var btn = document.getElementById('notificationBellBtn');
    var popup = document.getElementById('notificationPopup');
    if (!wrapper || !btn || !popup) return;

    var isOpen = false;

    function updateBellBadge() {
        // Count remaining rows in the popup by class
        var actionableRows = popup.querySelectorAll('.notification-row-actionable');
        var informationalRows = popup.querySelectorAll('.notification-row-informational');
        var badge = btn.querySelector('.notification-badge');
        var actionableCount = actionableRows.length;
        var informationalCount = informationalRows.length;

        // Remove existing badge
        if (badge) badge.remove();

        if (actionableCount > 0) {
            var newBadge = document.createElement('span');
            newBadge.className = 'notification-badge notification-badge-danger';
            newBadge.textContent = actionableCount > 9 ? '9+' : actionableCount.toString();
            btn.appendChild(newBadge);
        } else if (informationalCount > 0) {
            var dot = document.createElement('span');
            dot.className = 'notification-badge notification-badge-dot';
            btn.appendChild(dot);
        }
    }

    function openPopup() {
        popup.style.display = 'block';
        btn.setAttribute('aria-expanded', 'true');
        isOpen = true;
        var content = document.getElementById('notificationPopupContent');
        if (content) content.innerHTML = '<div class="text-center py-3 text-muted"><i class="fa-solid fa-spinner fa-spin"></i></div>';
        fetch('/Notifications/Popup', { redirect: 'error' })
            .then(function (r) {
                if (!r.ok) throw new Error(r.status);
                return r.text();
            })
            .then(function (html) {
                if (content) content.innerHTML = html;
                bindPopupClose();
                bindPopupDismiss();
                bindPopupMarkAllRead();
                trapFocus();
            })
            .catch(function () {
                if (content) content.innerHTML = '<div class="text-center py-3 text-muted"><i class="fa-solid fa-bell text-muted mb-2" style="font-size:1.5rem"></i><p class="mb-0 small">Could not load notifications.</p></div>';
            });
    }

    function closePopup() {
        popup.style.display = 'none';
        btn.setAttribute('aria-expanded', 'false');
        isOpen = false;
        btn.focus();
    }

    function bindPopupClose() {
        var closeBtn = document.getElementById('notificationPopupClose');
        if (closeBtn) {
            closeBtn.addEventListener('click', closePopup);
        }
    }

    function bindPopupDismiss() {
        popup.querySelectorAll('[data-ajax-dismiss]').forEach(function (dismissBtn) {
            dismissBtn.addEventListener('click', function (e) {
                e.preventDefault();
                var form = dismissBtn.closest('form');
                fetch(form.action, {
                    method: 'POST',
                    headers: { 'X-Requested-With': 'XMLHttpRequest' },
                    body: new FormData(form)
                }).then(function (r) {
                    if (r.ok) {
                        var row = dismissBtn.closest('.notification-row');
                        if (row) row.remove();
                        updateBellBadge();
                    }
                });
            });
        });
    }

    function bindPopupMarkAllRead() {
        popup.querySelectorAll('[data-ajax-markallread]').forEach(function (markBtn) {
            markBtn.addEventListener('click', function (e) {
                e.preventDefault();
                var form = markBtn.closest('form');
                fetch(form.action, {
                    method: 'POST',
                    headers: { 'X-Requested-With': 'XMLHttpRequest' },
                    body: new FormData(form)
                }).then(function (r) {
                    if (r.ok) {
                        // Remove badge — all marked as read
                        var badge = btn.querySelector('.notification-badge');
                        if (badge) badge.remove();
                        closePopup();
                    }
                });
            });
        });
    }

    function trapFocus() {
        var focusable = popup.querySelectorAll('a, button, input, [tabindex]:not([tabindex="-1"])');
        if (focusable.length > 0) focusable[0].focus();
    }

    btn.addEventListener('click', function (e) {
        e.stopPropagation();
        if (isOpen) closePopup(); else openPopup();
    });

    // Close on outside click
    document.addEventListener('click', function (e) {
        if (isOpen && !wrapper.contains(e.target)) closePopup();
    });

    // Close on Esc
    document.addEventListener('keydown', function (e) {
        if (isOpen && e.key === 'Escape') {
            e.preventDefault();
            closePopup();
        }
    });

    // Keyboard navigation for rows
    popup.addEventListener('keydown', function (e) {
        if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
            var rows = Array.from(popup.querySelectorAll('.notification-row'));
            var current = document.activeElement ? document.activeElement.closest('.notification-row') : null;
            var idx = current ? rows.indexOf(current) : -1;
            var next = e.key === 'ArrowDown' ? idx + 1 : idx - 1;
            if (next >= 0 && next < rows.length) {
                var btn2 = rows[next].querySelector('a, button');
                if (btn2) btn2.focus();
                e.preventDefault();
            }
        }
    });
})();

// Show a Bootstrap toast notification
// Usage: showToast('Success!', 'success') or showToast('Error!', 'danger')
function showToast(message, type) {
    type = type || 'success';
    var container = document.getElementById('toastContainer');
    if (!container) return;
    var iconClass = type === 'success' ? 'fa-check-circle text-success' : 'fa-exclamation-circle text-danger';
    var toastEl = document.createElement('div');
    toastEl.className = 'toast align-items-center border-0';
    toastEl.setAttribute('role', 'alert');
    toastEl.setAttribute('aria-live', 'assertive');
    toastEl.setAttribute('aria-atomic', 'true');
    var wrapper = document.createElement('div');
    wrapper.className = 'd-flex';
    var body = document.createElement('div');
    body.className = 'toast-body';
    var icon = document.createElement('i');
    icon.className = 'fa-solid ' + iconClass + ' me-2';
    body.appendChild(icon);
    body.appendChild(document.createTextNode(message));
    wrapper.appendChild(body);
    var closeBtn = document.createElement('button');
    closeBtn.type = 'button';
    closeBtn.className = 'btn-close me-2 m-auto';
    closeBtn.setAttribute('data-bs-dismiss', 'toast');
    closeBtn.setAttribute('aria-label', 'Close');
    wrapper.appendChild(closeBtn);
    toastEl.appendChild(wrapper);
    container.appendChild(toastEl);
    var toast = new bootstrap.Toast(toastEl, { delay: 4000 });
    toast.show();
    toastEl.addEventListener('hidden.bs.toast', function () { toastEl.remove(); });
}

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
            fetch('/Profile/' + userId + '/Popover')
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
