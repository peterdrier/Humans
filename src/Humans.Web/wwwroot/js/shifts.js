(function () {
  function token() {
    var el = document.querySelector('input[name="__RequestVerificationToken"]');
    return el ? el.value : '';
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
  document.addEventListener('click', function (e) {
    var btn = e.target.closest('.js-day-toggle');
    if (!btn || btn.disabled) return;
    btn.disabled = true;
    var original = btn.innerHTML;
    btn.innerHTML = '<span class="spinner-border" style="width:14px;height:14px"></span>';
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
      if (count !== null) {
        var badge = document.querySelector('.shifts-nav-tabs .badge');
        if (badge) badge.textContent = count;
      }
      return r.text();
    }).then(function (html) {
      if (html === null || html === undefined) return;
      var row = btn.closest('tr');
      if (row) row.outerHTML = html;
    }).catch(function () {
      btn.disabled = false;
      btn.innerHTML = original;
      showToast('error', 'Something went wrong. Please try again.');
    });
  });
})();
