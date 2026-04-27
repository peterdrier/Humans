// Sidebar DOM management for the container placement map.
// Renders unplaced/placed container cards and handles card clicks.

const unplacedEl = document.getElementById('sidebar-unplaced');
const placedEl   = document.getElementById('sidebar-placed');

let _containers  = [];    // current container data array
let _activeId    = null;  // currently active container ID
let _onActivate  = null;  // callback(container) when user clicks an unplaced card
let _onClear     = null;  // callback(container) when user clicks "Clear placement"
let _onSelect    = null;  // callback(container) when user clicks a placed card

/**
 * Initialise the sidebar with callbacks.
 * @param {function} onActivate - called with container object when unplaced card is clicked
 * @param {function} onClear    - called with container object when "Clear" button is clicked
 * @param {function} onSelect   - called with container object when placed card is clicked
 */
export function initSidebar(onActivate, onClear, onSelect) {
    _onActivate = onActivate;
    _onClear    = onClear;
    _onSelect   = onSelect;
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
    const unplaced = _containers.filter(c => !c.locationGeoJson);
    const placed   = _containers.filter(c =>  c.locationGeoJson);

    // Keep the section label, replace the rest
    const unplacedLabel = unplacedEl.querySelector('.sidebar-section-label');
    unplacedEl.innerHTML = '';
    if (unplacedLabel) unplacedEl.appendChild(unplacedLabel);

    const placedLabel = placedEl.querySelector('.sidebar-section-label');
    placedEl.innerHTML = '';
    if (placedLabel) placedEl.appendChild(placedLabel);

    for (const c of unplaced) {
        const isActive = c.id === _activeId;
        const card = document.createElement('div');
        card.className = 'container-card' + (isActive ? ' active' : '');
        card.dataset.id = c.id;
        card.innerHTML = `
            <div class="container-card-name">${escHtml(c.name)}</div>
            ${c.description ? `<div class="container-card-desc">${escHtml(c.description)}</div>` : ''}
        `;
        if (!isActive && c.canEdit) {
            card.addEventListener('click', () => _onActivate?.(c));
        }
        unplacedEl.appendChild(card);
    }

    for (const c of placed) {
        const card = document.createElement('div');
        card.className = 'container-card placed';
        card.dataset.id = c.id;
        card.innerHTML = `
            <div class="container-card-name placed-name">✓ ${escHtml(c.name)}</div>
            ${c.description ? `<div class="container-card-desc">${escHtml(c.description)}</div>` : ''}
            ${c.canEdit ? `<div class="container-card-actions">
                <button class="btn btn-outline-danger btn-sm" style="font-size:11px;padding:2px 8px;"
                    data-clear-id="${c.id}">Clear placement</button>
            </div>` : ''}
        `;
        if (c.canEdit) {
            card.style.cursor = 'pointer';
            card.addEventListener('click', (e) => {
                if (e.target.closest('[data-clear-id]')) return; // handled below
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
        placedEl.appendChild(card);
    }
}

function escHtml(str) {
    return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}
