// Sidebar DOM management for the container placement map.
// Renders containers grouped by barrio, split into Unplaced / Placed sections.

const unplacedEl = document.getElementById('sidebar-unplaced');
const placedEl   = document.getElementById('sidebar-placed');

let _containers        = [];    // current container data array
let _campNames         = {};    // campSeasonId → campName
let _activeId          = null;  // currently active container ID
let _filterCampSeasonId = null; // if set, sidebar only shows this barrio's containers
let _onActivate  = null;  // callback(container) when user clicks an unplaced card
let _onClear     = null;  // callback(container) when user clicks "Clear placement"
let _onSelect    = null;  // callback(container) when user clicks a placed card
let _onLocate    = null;  // callback(container) when user clicks the locate button

export function initSidebar(onActivate, onClear, onSelect, onLocate, filterCampSeasonId = null) {
    _onActivate         = onActivate;
    _onClear            = onClear;
    _onSelect           = onSelect;
    _onLocate           = onLocate;
    _filterCampSeasonId = filterCampSeasonId;
}

/** Provide the campSeasonId → campName lookup used for barrio group headers. */
export function setCampNames(campNames) {
    _campNames = campNames;
}

/** Replace the container list and re-render. */
export function setContainers(containers) {
    _containers = containers;
    render();
}

/** Mark a container as having been successfully placed (moves card to Placed). */
export function markPlaced(_id) {
    _activeId = null;
    render();
}

/** Mark a container as unplaced (moves card back to Unplaced). */
export function markUnplaced(_id) {
    render();
}

/** Highlight the active card (currently being placed). */
export function setActiveId(id) {
    _activeId = id;
    render();
}

/** Scroll the placed section to show a specific container card. */
export function scrollToPlaced(id) {
    const card = placedEl.querySelector(`[data-id="${id}"]`);
    if (card) card.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
}

function render() {
    const visible = _filterCampSeasonId
        ? _containers.filter(c => c.campSeasonId === _filterCampSeasonId)
        : _containers;
    renderSection(unplacedEl, visible.filter(c => !c.locationGeoJson), false);
    renderSection(placedEl,   visible.filter(c =>  c.locationGeoJson), true);
}

function renderSection(sectionEl, items, isPlaced) {
    const label = sectionEl.querySelector('.sidebar-section-label');
    sectionEl.innerHTML = '';
    if (label) sectionEl.appendChild(label);

    const groups = groupByCamp(items);
    const showHeaders = groups.size > 1;

    for (const [campSeasonId, groupItems] of groups) {
        if (showHeaders) {
            const hdr = document.createElement('div');
            hdr.className = 'list-group-item py-1 px-3 small text-muted bg-body-secondary';
            hdr.textContent = campSeasonId === null
                ? 'Nobodies'
                : (_campNames[campSeasonId] ?? campSeasonId);
            sectionEl.appendChild(hdr);
        }
        for (const c of groupItems) {
            sectionEl.appendChild(isPlaced ? makePlacedCard(c) : makeUnplacedCard(c));
        }
    }
}

function groupByCamp(items) {
    const groups = new Map();
    for (const c of items) {
        const key = c.campSeasonId ?? null;
        if (!groups.has(key)) groups.set(key, []);
        groups.get(key).push(c);
    }
    // Org containers (null key) first, then barrios sorted by name
    return new Map([...groups.entries()].sort(([a], [b]) => {
        if (a === null) return -1;
        if (b === null) return 1;
        return (_campNames[a] ?? '').localeCompare(_campNames[b] ?? '');
    }));
}

function makeUnplacedCard(c) {
    const isActive = c.id === _activeId;
    const canClick = !isActive && c.canEdit;
    const card = document.createElement(canClick ? 'button' : 'div');
    if (canClick) card.type = 'button';
    card.className = 'list-group-item'
        + (isActive ? ' active' : '')
        + (canClick ? ' list-group-item-action' : '');
    card.dataset.id = c.id;
    card.innerHTML = `
        <div class="fw-semibold small">${escHtml(c.name)}</div>
        ${c.description ? `<div class="small text-truncate ${isActive ? 'opacity-75' : 'text-muted'}">${escHtml(c.description)}</div>` : ''}
    `;
    if (canClick) card.addEventListener('click', () => _onActivate?.(c));
    return card;
}

function makePlacedCard(c) {
    const card = document.createElement('div');
    card.className = 'list-group-item list-group-item-success'
        + (c.canEdit ? ' list-group-item-action' : '');
    card.dataset.id = c.id;
    card.innerHTML = `
        <div class="fw-semibold small">✓ ${escHtml(c.name)}</div>
        ${c.description ? `<div class="small text-truncate opacity-75">${escHtml(c.description)}</div>` : ''}
        <div class="mt-1 d-flex gap-1">
            <button class="btn btn-outline-secondary btn-sm py-0 px-2" style="font-size:11px;"
                data-locate-id="${c.id}" title="Center map on this container">
                <i class="fa-solid fa-location-dot"></i>
            </button>
            ${c.canEdit ? `<button class="btn btn-outline-danger btn-sm py-0 px-2" style="font-size:11px;"
                data-clear-id="${c.id}">Clear placement</button>` : ''}
        </div>
    `;
    const locateBtn = card.querySelector('[data-locate-id]');
    if (locateBtn) {
        locateBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            _onLocate?.(c);
        });
    }
    if (c.canEdit) {
        card.addEventListener('click', (e) => {
            if (e.target.closest('[data-clear-id]') || e.target.closest('[data-locate-id]')) return;
            _onSelect?.(c);
        });
        const clearBtn = card.querySelector('[data-clear-id]');
        if (clearBtn) {
            clearBtn.addEventListener('click', (e) => {
                e.stopPropagation();
                _onClear?.(c);
            });
        }
    }
    return card;
}

function escHtml(str) {
    return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}
