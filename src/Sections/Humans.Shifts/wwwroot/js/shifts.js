(function () {
  function token() {
    var el = document.querySelector('input[name="__RequestVerificationToken"]');
    return el ? el.value : '';
  }
  // The server renders the badge only when count > 0, so it must be addable and removable here.
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
  // Localized, from the page's data-error-msg.
  function errorMsg() {
    var c = document.querySelector('.shifts-toast-container');
    return (c && c.getAttribute('data-error-msg')) || 'Something went wrong. Please try again.';
  }
  document.addEventListener('click', function (e) {
    var btn = e.target.closest('.js-day-toggle');
    if (!btn || btn.disabled) return;
    btn.disabled = true;
    // Prefix a spinner to the label: no collapse to icon width, and the accessible name stays.
    var original = btn.innerHTML;
    btn.innerHTML = '<span class="spinner-border spinner-border-sm me-1" role="status" aria-hidden="true"></span>' + original;
    var fd = new FormData();
    fd.append('__RequestVerificationToken', token());
    fd.append('shiftId', btn.dataset.shiftId);
    fetch('/Shifts/ToggleDay', {
      method: 'POST',
      redirect: 'error',
      headers: { 'X-Requested-With': 'XMLHttpRequest' },
      body: fd
    }).then(function (r) {
      if (!r.ok) throw new Error("HTTP " + r.status);
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
      // The fragment carries a fresh description row, so drop the stale sibling or they pile up on each toggle.
      var sib = row.nextElementSibling;
      if (sib && sib.querySelector('td.shift-description')) sib.remove();
      // Insert-then-remove, not outerHTML, keeps a handle on the new node to restore focus; outerHTML drops it to <body>.
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
