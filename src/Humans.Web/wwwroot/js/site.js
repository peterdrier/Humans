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

// Declarative client-side table sorting.
// Add data-sortable-table to a table and data-sort / data-sort-col / data-sort-key
// to header cells. Cell data-sort-value/data-value or row data-* values override text.
(function () {
    function textForSort(value) {
        return (value || '').trim();
    }

    function numberForSort(value) {
        var normalized = textForSort(value).replace(/[,%\s€]/g, '');
        var parsed = parseFloat(normalized);
        return isNaN(parsed) ? null : parsed;
    }

    function valueFromRow(row, columnIndex, key) {
        if (key && row.dataset && row.dataset[key] !== undefined) {
            return row.dataset[key];
        }

        var cell = row.cells[columnIndex];
        if (!cell) return '';
        return cell.dataset.sortValue || cell.dataset.value || cell.textContent || '';
    }

    function compareValues(a, b, sortType) {
        var aNumber = numberForSort(a);
        var bNumber = numberForSort(b);
        var numeric = sortType === 'number' || (sortType !== 'text' && aNumber !== null && bNumber !== null);

        if (numeric) {
            return (aNumber || 0) - (bNumber || 0);
        }

        return textForSort(a).localeCompare(textForSort(b));
    }

    document.querySelectorAll('table[data-sortable-table]').forEach(function (table) {
        var tbody = table.tBodies[0];
        if (!tbody) return;

        var headers = table.querySelectorAll('th[data-sort], th[data-sort-col], th[data-sort-key]');
        headers.forEach(function (th) {
            th.classList.add('sortable-table-header');
            th.setAttribute('role', th.getAttribute('role') || 'button');
            th.setAttribute('tabindex', th.getAttribute('tabindex') || '0');

            function sort() {
                var columnIndex = th.dataset.sortCol !== undefined
                    ? parseInt(th.dataset.sortCol, 10)
                    : Array.prototype.indexOf.call(th.parentElement.children, th);
                if (isNaN(columnIndex) || columnIndex < 0) return;

                var key = th.dataset.sortKey;
                var sortType = th.dataset.sortType || 'auto';
                var nextDirection = th.getAttribute('data-sort-dir') === 'asc' ? 'desc' : 'asc';
                var directionMultiplier = nextDirection === 'asc' ? 1 : -1;

                headers.forEach(function (header) {
                    header.removeAttribute('data-sort-dir');
                    header.setAttribute('aria-sort', 'none');
                    header.classList.remove('sort-asc', 'sort-desc');
                    var indicator = header.querySelector('.sort-indicator');
                    if (indicator) indicator.textContent = '';
                });

                th.setAttribute('data-sort-dir', nextDirection);
                th.setAttribute('aria-sort', nextDirection === 'asc' ? 'ascending' : 'descending');
                th.classList.add(nextDirection === 'asc' ? 'sort-asc' : 'sort-desc');
                var activeIndicator = th.querySelector('.sort-indicator');
                if (activeIndicator) activeIndicator.textContent = nextDirection === 'asc' ? ' ▲' : ' ▼';

                var rows = Array.from(tbody.querySelectorAll('tr'));
                rows.sort(function (a, b) {
                    var aValue = valueFromRow(a, columnIndex, key);
                    var bValue = valueFromRow(b, columnIndex, key);
                    return compareValues(aValue, bValue, sortType) * directionMultiplier;
                });

                rows.forEach(function (row) { tbody.appendChild(row); });
            }

            th.addEventListener('click', sort);
            th.addEventListener('keydown', function (e) {
                if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    sort();
                }
            });
        });
    });
})();

// Declarative client-side table filtering (companion to data-sortable-table).
// _Table.cshtml emits a .table-component wrapper; inside it:
//   input[data-table-search]      — global contains-search across all cells
//   [data-filter-col="<index>"]   — per-column filter (input = contains, select = exact)
// Selects rendered with no options are auto-populated from distinct column values.
(function () {
    function norm(value) {
        return (value || '').trim().toLowerCase();
    }

    function applyFilters(table, search, filters) {
        var term = norm(search && search.value);
        Array.from(table.tBodies[0].rows).forEach(function (row) {
            var visible = !term || norm(row.textContent).indexOf(term) !== -1;
            filters.forEach(function (filter) {
                if (!visible) return;
                var wanted = norm(filter.input.value);
                if (!wanted) return;
                var cell = row.cells[filter.col];
                var text = norm(cell && cell.textContent);
                visible = filter.exact ? text === wanted : text.indexOf(wanted) !== -1;
            });
            row.style.display = visible ? '' : 'none';
        });
    }

    document.querySelectorAll('.table-component').forEach(function (root) {
        var table = root.querySelector('table');
        if (!table || !table.tBodies[0]) return;

        var search = root.querySelector('input[data-table-search]');
        var filters = [];

        root.querySelectorAll('[data-filter-col]').forEach(function (input) {
            var col = parseInt(input.dataset.filterCol, 10);
            if (isNaN(col)) return;
            var exact = input.tagName === 'SELECT';

            if (exact && input.options.length <= 1) {
                var seen = {};
                Array.from(table.tBodies[0].rows).forEach(function (row) {
                    var text = (row.cells[col] ? row.cells[col].textContent : '').trim();
                    if (text && text !== '—' && !seen[text]) {
                        seen[text] = true;
                        var option = document.createElement('option');
                        option.value = text;
                        option.textContent = text;
                        input.appendChild(option);
                    }
                });
            }

            filters.push({ input: input, col: col, exact: exact });
            input.addEventListener(exact ? 'change' : 'input', function () {
                applyFilters(table, search, filters);
            });
        });

        if (search) {
            var debounce;
            search.addEventListener('input', function () {
                clearTimeout(debounce);
                debounce = setTimeout(function () { applyFilters(table, search, filters); }, 150);
            });
        }
    });
})();

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

// Initialize Bootstrap tooltips for any element with data-bs-toggle="tooltip"
(function () {
    if (typeof bootstrap === 'undefined' || !bootstrap.Tooltip) return;
    document.querySelectorAll('[data-bs-toggle="tooltip"]').forEach(function (el) {
        new bootstrap.Tooltip(el);
    });
})();

// Human profile popover (lazy-loaded on first hover)
(function () {
    var cache = {};
    document.addEventListener('mouseenter', function (e) {
        // Document-level mouseenter bubbles up with e.target === document
        // for cursor entries that don't land on an Element; guard so
        // calling .closest() on a non-Element doesn't throw.
        if (!e.target || typeof e.target.closest !== 'function') return;
        // Public-popover variant (#771) serves a reduced partial to anonymous
        // viewers on public team pages. The full popover is preferred when
        // both attributes are present (e.g. logged-in admin previewing).
        var el = e.target.closest('[data-human-popover], [data-human-popover-public]');
        if (!el || el._popoverInit) return;
        el._popoverInit = true;

        var userId = el.getAttribute('data-user-id');
        if (!userId) return;

        var isPublic = !el.hasAttribute('data-human-popover')
            && el.hasAttribute('data-human-popover-public');
        var endpoint = isPublic
            ? '/Profile/' + userId + '/PublicPopover'
            : '/Profile/' + userId + '/Popover';
        var cacheKey = (isPublic ? 'public:' : 'full:') + userId;

        var popover = new bootstrap.Popover(el, {
            trigger: 'hover focus',
            placement: 'auto',
            html: true,
            content: '<div class="text-center p-2"><div class="spinner-border spinner-border-sm"></div></div>',
            sanitize: false
        });
        popover.show();

        if (cache[cacheKey]) {
            popover.setContent({ '.popover-body': cache[cacheKey] });
        } else {
            fetch(endpoint)
                .then(function (r) {
                    // 404 from PublicPopover means "no public role" — suppress
                    // the spinner tooltip instead of showing an error.
                    if (r.status === 404) {
                        popover.dispose();
                        return null;
                    }
                    return r.ok ? r.text() : '';
                })
                .then(function (html) {
                    if (html) {
                        cache[cacheKey] = html;
                        popover.setContent({ '.popover-body': html });
                    }
                });
        }
    }, true);
})();

// Admin sidebar — desktop accordion (collapse state in localStorage) and
// mobile two-tier strip (group chips switch which item row is shown).
// Horizontal scroll affordances per row: data-scroll-start/end on the row
// wrapper drives the edge fades in admin-shell.css, plus mouse drag-to-scroll.
(function () {
    var sidebar = document.querySelector('body.admin-shell .sidebar');
    if (!sidebar) return;

    // ── Desktop accordion ─────────────────────────────────────────────
    var STORE_KEY = 'adminNavCollapsed';
    var stored = {};
    try { stored = JSON.parse(localStorage.getItem(STORE_KEY) || '{}') || {}; } catch (e) { /* corrupt state — fall back to defaults */ }

    sidebar.querySelectorAll('.nav-group').forEach(function (section) {
        var label = section.dataset.group;
        var toggle = section.querySelector('.group-toggle');
        if (!toggle) return;
        // Apply the remembered state, but never hide the active page's group.
        if (Object.prototype.hasOwnProperty.call(stored, label) && !section.querySelector('a.active')) {
            section.classList.toggle('collapsed', !!stored[label]);
        }
        toggle.setAttribute('aria-expanded', section.classList.contains('collapsed') ? 'false' : 'true');
        toggle.addEventListener('click', function () {
            var collapsed = section.classList.toggle('collapsed');
            toggle.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
            stored[label] = collapsed;
            try { localStorage.setItem(STORE_KEY, JSON.stringify(stored)); } catch (e) { /* private mode */ }
        });
    });

    // ── Horizontal scroll affordances (mobile rows) ───────────────────
    // Mouse drag-to-scroll: native overflow-x:auto handles touch panning on
    // real phones, but a mouse (or DevTools mobile emulation without touch
    // sim) can't drag-scroll without JS. Threshold is 5px so a tiny jitter
    // during a click doesn't suppress navigation.
    function attachHScroll(wrapper, scroll) {
        function update() {
            var atStart = scroll.scrollLeft <= 1;
            var atEnd = scroll.scrollLeft + scroll.clientWidth >= scroll.scrollWidth - 1;
            wrapper.dataset.scrollStart = atStart ? 'true' : 'false';
            wrapper.dataset.scrollEnd = atEnd ? 'true' : 'false';
        }

        scroll.addEventListener('scroll', update, { passive: true });
        window.addEventListener('resize', update);

        var dragStartX = 0;
        var dragStartScroll = 0;
        var dragMoved = 0;
        var dragging = false;

        scroll.addEventListener('mousedown', function (e) {
            if (e.button !== 0) return;
            dragging = true;
            dragStartX = e.pageX;
            dragStartScroll = scroll.scrollLeft;
            dragMoved = 0;
            scroll.style.cursor = 'grabbing';
            scroll.style.userSelect = 'none';
        });

        document.addEventListener('mousemove', function (e) {
            if (!dragging) return;
            var dx = e.pageX - dragStartX;
            if (Math.abs(dx) > Math.abs(dragMoved)) dragMoved = dx;
            scroll.scrollLeft = dragStartScroll - dx;
        });

        function endDrag() {
            if (!dragging) return;
            dragging = false;
            scroll.style.cursor = '';
            scroll.style.userSelect = '';
        }
        document.addEventListener('mouseup', endDrag);
        scroll.addEventListener('mouseleave', endDrag);

        // Suppress the click that follows a real drag, so dragging across a
        // nav link doesn't accidentally navigate.
        scroll.addEventListener('click', function (e) {
            if (Math.abs(dragMoved) > 5) {
                e.preventDefault();
                e.stopPropagation();
                dragMoved = 0;
            }
        }, true);

        update();
        return update;
    }

    var rowUpdates = {};
    var chipsWrap = sidebar.querySelector('.group-chips-wrap');
    var chipsRow = sidebar.querySelector('.group-chips');
    if (chipsWrap && chipsRow) attachHScroll(chipsWrap, chipsRow);
    sidebar.querySelectorAll('.nav-group').forEach(function (section) {
        var items = section.querySelector('.group-items');
        if (items) rowUpdates[section.dataset.group] = attachHScroll(section, items);
    });

    // ── Mobile group chips ────────────────────────────────────────────
    sidebar.querySelectorAll('.group-chip').forEach(function (chip) {
        chip.addEventListener('click', function () {
            sidebar.querySelectorAll('.group-chip').forEach(function (c) {
                c.classList.toggle('m-active', c === chip);
                c.setAttribute('aria-selected', c === chip ? 'true' : 'false');
            });
            sidebar.querySelectorAll('.nav-group').forEach(function (s) {
                s.classList.toggle('m-active', s.dataset.group === chip.dataset.group);
            });
            // The newly shown row was display:none during initial measurement.
            var refresh = rowUpdates[chip.dataset.group];
            if (refresh) refresh();
        });
    });

    // On mobile, bring the active chip and item into view if off-screen so
    // users land on the right entry without manual scrolling.
    var horizontal = window.matchMedia('(max-width: 767.98px)');
    if (horizontal.matches) {
        [sidebar.querySelector('.group-chip.m-active'), sidebar.querySelector('.nav-group.m-active a.active')]
            .forEach(function (el) {
                if (!el) return;
                var row = el.closest('.group-chips') || el.closest('.group-items');
                if (!row) return;
                var prev = row.style.scrollBehavior;
                row.style.scrollBehavior = 'auto';
                el.scrollIntoView({ inline: 'center', block: 'nearest' });
                row.style.scrollBehavior = prev;
            });
    }
})();

// Expand/collapse compressed date ranges in _BuildStrikeRotaTable.
// Used by /Shifts/Index and /OnboardingWidget/Shifts; no-op elsewhere.
(function () {
    document.querySelectorAll('.shift-range-header').forEach(function (el) {
        el.addEventListener('click', function () {
            var rangeKey = this.getAttribute('data-range');
            var icon = this.querySelector('.range-icon');
            var rows = document.querySelectorAll('.range-detail-' + rangeKey);
            if (rows.length === 0) return;
            var expanding = rows[0].classList.contains('d-none');
            rows.forEach(function (row) { row.classList.toggle('d-none', !expanding); });
            if (icon) {
                icon.classList.toggle('fa-chevron-right', !expanding);
                icon.classList.toggle('fa-chevron-down', expanding);
            }
        });
    });
})();

// Favourite hearts (Events): toggle the favourite in place — or remove the row
// on My Schedule — via the JSON favourites API, so the action never reloads the
// page and the user's filters/scroll survive. The JS contract is rendered by
// Views/Shared/_FavouriteButton.cshtml. The API is same-origin + cookie-auth and
// carries no antiforgery requirement.
(function () {
    function removeRow(btn) {
        // A whole-event unfavourite (empty data-day) deletes every occurrence
        // server-side, so remove every occurrence row for this event; a day-specific
        // unfavourite removes only the clicked row.
        var day = btn.getAttribute('data-day');
        var toggles = (day === null || day === '')
            ? document.querySelectorAll('.js-favourite-toggle[data-remove-row="true"][data-event-id="' + btn.getAttribute('data-event-id') + '"]')
            : [btn];
        Array.prototype.forEach.call(toggles, function (t) {
            var row = t.closest('.list-group-item');
            if (!row) return;
            var list = row.closest('.list-group');
            row.remove();
            // Drop an emptied day group (its heading + list) so no orphan date header lingers.
            if (list && !list.querySelector('.list-group-item')) {
                var heading = list.previousElementSibling;
                if (heading && heading.tagName === 'H4') heading.remove();
                list.remove();
            }
        });
        // Whole schedule cleared — reload so the canonical empty-state message renders.
        if (!document.querySelector('.js-favourite-toggle[data-remove-row="true"]')) window.location.reload();
    }

    document.addEventListener('click', function (e) {
        var btn = e.target.closest('.js-favourite-toggle');
        if (!btn || btn.disabled) return;

        var confirmMsg = btn.getAttribute('data-favourite-confirm');
        if (confirmMsg && !confirm(confirmMsg)) return;

        var favourited = btn.getAttribute('data-favourited') === 'true';
        var url = '/api/events/favourites/' + encodeURIComponent(btn.getAttribute('data-event-id'));
        var day = btn.getAttribute('data-day');
        if (day !== null && day !== '') url += '?day=' + encodeURIComponent(day);

        btn.disabled = true;
        fetch(url, { method: favourited ? 'DELETE' : 'POST' })
            .then(function (r) {
                if (!r.ok) throw new Error(r.status);
                if (btn.getAttribute('data-remove-row') === 'true') { removeRow(btn); return; }
                var nowFav = !favourited;
                btn.setAttribute('data-favourited', nowFav ? 'true' : 'false');
                btn.setAttribute('aria-pressed', nowFav ? 'true' : 'false');
                btn.classList.toggle('btn-danger', nowFav);
                btn.classList.toggle('btn-outline-danger', !nowFav);
                var label = btn.getAttribute(nowFav ? 'data-title-remove' : 'data-title-add');
                if (label) { btn.title = label; btn.setAttribute('aria-label', label); }
            })
            .catch(function () { showToast(btn.getAttribute('data-error') || 'Something went wrong.', 'danger'); })
            .finally(function () { btn.disabled = false; });
    });
})();
