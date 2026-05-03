(function () {
    document.querySelectorAll('[data-delete-id]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var id = btn.getAttribute('data-delete-id');
            if (!confirm('Delete this conversation?')) return;
            fetch('/Agent/Conversation/' + id, {
                method: 'DELETE',
                headers: { 'RequestVerificationToken': document.querySelector('input[name="__RequestVerificationToken"]').value }
            }).then(function (r) {
                if (r.ok) document.querySelector('tr[data-id="' + id + '"]').remove();
                else showToast('Delete failed.', 'danger');
            });
        });
    });
})();
