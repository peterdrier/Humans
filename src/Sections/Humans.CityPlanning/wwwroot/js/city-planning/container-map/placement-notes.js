// Placement-notes modal: view & edit PlacementNotes + PlacementImage for a placed container.

import { CONFIG }                from './config.js';
import { updatePlacementNotes }  from './api.js';

let _modalEl       = null;
let _form          = null;
let _openVersion   = 0;
let _current       = null;   // container currently shown in the modal
let _onSaved       = null;   // callback(container.id, { placementNotes, placementImageUrl, placementImageFileName })
let _bound         = false;

export function initPlacementNotes(onSaved) {
    _modalEl = document.getElementById('placementNotesModal');
    if (!_modalEl) return;
    _form = _modalEl.querySelector('#placementNotesForm');
    _onSaved = onSaved;
    if (!_bound) {
        _form.addEventListener('submit', onSubmit);
        _bound = true;
    }
}

export function openPlacementNotes(container) {
    if (!_modalEl) return;
    _openVersion++;
    _current = container;

    _modalEl.querySelector('[data-role="container-name"]').textContent = container.name;
    _form.reset();
    _form.querySelector('textarea[name="PlacementNotes"]').value = container.placementNotes ?? '';

    const wrap = _modalEl.querySelector('[data-role="existing-image-wrap"]');
    const img = _modalEl.querySelector('[data-role="existing-image"]');
    const removeWrap = _modalEl.querySelector('[data-role="remove-image-wrap"]');
    if (container.placementImageUrl) {
        img.src = container.placementImageUrl;
        img.alt = container.placementImageFileName ?? '';
        wrap.classList.remove('d-none');
        removeWrap.classList.remove('d-none');
    } else {
        wrap.classList.add('d-none');
        removeWrap.classList.add('d-none');
    }

    const err = _modalEl.querySelector('[data-role="error"]');
    err.classList.add('d-none');
    err.textContent = '';

    const fieldset = _modalEl.querySelector('[data-role="fields"]');
    const saveBtn = _modalEl.querySelector('[data-role="save-btn"]');
    const canEdit = !!container.canEdit;
    fieldset.disabled = !canEdit;
    saveBtn.disabled = false;
    saveBtn.classList.toggle('d-none', !canEdit);

    bootstrap.Modal.getOrCreateInstance(_modalEl).show();
}

async function onSubmit(e) {
    e.preventDefault();
    const containerId = _current.id;
    const version = _openVersion;
    const err = _modalEl.querySelector('[data-role="error"]');
    const saveBtn = _modalEl.querySelector('[data-role="save-btn"]');
    err.classList.add('d-none');
    saveBtn.disabled = true;
    try {
        const result = await updatePlacementNotes(containerId, CONFIG.YEAR, new FormData(_form));
        _onSaved?.(containerId, {
            placementNotes: result.placementNotes ?? null,
            placementImageUrl: result.placementImageUrl ?? null,
            placementImageFileName: result.placementImageFileName ?? null,
        });
        if (version === _openVersion) bootstrap.Modal.getOrCreateInstance(_modalEl).hide();
    } catch (ex) {
        if (version === _openVersion) {
            err.textContent = ex.message;
            err.classList.remove('d-none');
        }
    } finally {
        if (version === _openVersion) saveBtn.disabled = false;
    }
}
