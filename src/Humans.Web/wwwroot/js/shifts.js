(function () {
  function token() {
    var el = document.querySelector('input[name="__RequestVerificationToken"]');
    return el ? el.value : '';
  }
  // Create, update, or remove the "My Shifts" tab count badge. The server only
  // renders the badge when count > 0, so we must be able to add/remove it too.
  function updateMineBadge(count) {
    var tabs = document.querySelectorAll('.shifts-nav-tabs .nav-link');
    var mineTab = tabs.length > 1 ? tabs[tabs.length - 1] : null;
    if (!mineTab) return;
    var badge = mineTab.querySelector('.badge');
    if (count > 0) {
      if (!badge) {
        badge = document.createElement('span');
        badge.className = 'badge bg-primary ms-1';
        mineTab.appendChild(badge);
      }
      badge.textContent = count;
    } else if (badge) {
      badge.remove();
    }
  }
  function showToast(type, msg) {
    var c = document.querySelector('.shifts-toast-container');
    if (!c || !msg) return;
    var div = document.createElement('div');
    div.className = 'alert alert-' + (type === 'error' ? 'danger' : type);
    div.textContent = msg;
    c.appendChild(div);
    setTimeout(function () {
      if (window.bootstrap && bootstrap.Alert) bootstrap.Alert.getOrCreateInstance(div).close();
      else div.remove();
    }, 4000);
  }
  // Localized client-side network-error message, supplied by the page (data-error-msg).
  function errorMsg() {
    var c = document.querySelector('.shifts-toast-container');
    return (c && c.getAttribute('data-error-msg')) || 'Something went wrong. Please try again.';
  }
  document.addEventListener('click', function (e) {
    var btn = e.target.closest('.js-day-toggle');
    if (!btn || btn.disabled) return;
    btn.disabled = true;
    // Keep the existing label and prefix a small spinner — avoids the button
    // collapsing to icon-width (layout shift) and keeps an accessible name.
    var original = btn.innerHTML;
    btn.innerHTML = '<span class="spinner-border spinner-border-sm me-1" role="status" aria-hidden="true"></span>' + original;
    var fd = new FormData();
    fd.append('__RequestVerificationToken', token());
    fd.append('shiftId', btn.dataset.shiftId);
    fetch('/Shifts/ToggleDay', {
      method: 'POST',
      headers: { 'X-Requested-With': 'XMLHttpRequest' },
      body: fd
    }).then(function (r) {
      var redirect = r.headers.get('X-Redirect');
      if (redirect) { window.location = redirect; return null; }
      var toastType = r.headers.get('X-Toast-Type');
      var toastMsg = r.headers.get('X-Toast-Msg');
      var count = r.headers.get('X-My-Signup-Count');
      if (toastType && toastMsg) showToast(toastType, decodeURIComponent(toastMsg));
      if (count !== null) updateMineBadge(parseInt(count, 10));
      return r.text();
    }).then(function (html) {
      if (html === null || html === undefined) return;
      var row = btn.closest('tr');
      if (!row) return;
      // Event-shift rows are followed by their own description <tr> (td.shift-description).
      // The returned fragment already contains a fresh description row, so drop the stale
      // sibling first — otherwise the old one is orphaned and they accumulate on each toggle.
      // Build/Strike flat rows have no description sibling.
      var sib = row.nextElementSibling;
      if (sib && sib.querySelector('td.shift-description')) sib.remove();
      // Swap via insert-then-remove (not row.outerHTML) so we keep a handle on the new
      // node and can restore keyboard focus — outerHTML would drop focus to <body>.
      row.insertAdjacentHTML('afterend', html);
      var newRow = row.nextElementSibling;
      row.remove();
      var newBtn = newRow && newRow.querySelector('.js-day-toggle');
      if (newBtn && !newBtn.disabled) newBtn.focus();
    }).catch(function () {
      btn.disabled = false;
      btn.innerHTML = original;
      showToast('error', errorMsg());
    });
  });
})();
