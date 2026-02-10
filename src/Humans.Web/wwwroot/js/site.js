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

// Generic confirmation handler for [data-confirm] attributes
document.addEventListener('click', function (e) {
    var target = e.target.closest('[data-confirm]');
    if (target) {
        if (!confirm(target.getAttribute('data-confirm'))) {
            e.preventDefault();
            e.stopPropagation();
        }
    }
});

document.addEventListener('submit', function (e) {
    var form = e.target.closest('form[data-confirm]');
    if (form) {
        if (!confirm(form.getAttribute('data-confirm'))) {
            e.preventDefault();
            e.stopPropagation();
        }
    }
});
