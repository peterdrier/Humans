// admin-import.js — GeoJSON bulk import for the City Planning Admin page.
// Depends on: turf (global), Bootstrap 5 modal (global).

const fileInput   = document.getElementById('import-file-input');
const previewBtn  = document.getElementById('import-preview-btn');
const errorDiv    = document.getElementById('import-error');

let pendingImport = null;

function showError(msg) {
    errorDiv.textContent = msg;
    errorDiv.classList.remove('d-none');
}

function clearError() {
    errorDiv.textContent = '';
    errorDiv.classList.add('d-none');
}

function formatArea(sqm) {
    if (sqm == null) return '—';
    return Math.round(sqm).toLocaleString() + ' m²';
}

function buildCampLookup(state) {
    const lookup = new Map();
    for (const p of state.campPolygons ?? []) {
        lookup.set(p.campName.toLowerCase(),  { campSeasonId: p.campSeasonId, campName: p.campName, currentAreaSqm: p.areaSqm });
        lookup.set(p.campSlug.toLowerCase(),  { campSeasonId: p.campSeasonId, campName: p.campName, currentAreaSqm: p.areaSqm });
    }
    for (const s of state.campSeasonsWithoutPolygon ?? []) {
        lookup.set(s.campName.toLowerCase(), { campSeasonId: s.campSeasonId, campName: s.campName, currentAreaSqm: null });
        lookup.set(s.campSlug.toLowerCase(), { campSeasonId: s.campSeasonId, campName: s.campName, currentAreaSqm: null });
    }
    return lookup;
}

function matchFeatures(features, lookup) {
    const matched = [];
    const unrecognized = [];
    const seenIds = new Set();

    for (const feature of features) {
        const props = feature.properties ?? {};
        const name  = (props.campName ?? '').toLowerCase();
        const slug  = (props.campSlug ?? '').toLowerCase();
        const camp  = lookup.get(name) || lookup.get(slug);

        if (!camp) {
            unrecognized.push(props.campName || props.campSlug || '(unnamed feature)');
            continue;
        }
        if (seenIds.has(camp.campSeasonId)) continue;
        seenIds.add(camp.campSeasonId);

        const newAreaSqm = turf.area(feature);
        matched.push({
            campSeasonId:    camp.campSeasonId,
            campName:        camp.campName,
            previousAreaSqm: camp.currentAreaSqm,
            newAreaSqm,
            geoJson:         JSON.stringify(feature),
        });
    }

    return { matched, unrecognized };
}

async function handlePreview() {
    clearError();
    const file = fileInput.files?.[0];
    if (!file) { showError('Please select a GeoJSON file.'); return; }

    let parsed;
    try {
        parsed = JSON.parse(await file.text());
    } catch {
        showError('Invalid file — not valid JSON.'); return;
    }
    if (parsed?.type !== 'FeatureCollection' || !Array.isArray(parsed.features)) {
        showError('File must be a GeoJSON FeatureCollection.'); return;
    }

    let state;
    try {
        const resp = await fetch('/api/city-planning/state');
        if (!resp.ok) throw new Error();
        state = await resp.json();
    } catch {
        showError('Could not load camp list. Please try again.'); return;
    }

    const lookup = buildCampLookup(state);
    const { matched, unrecognized } = matchFeatures(parsed.features, lookup);
    pendingImport = { matched, unrecognized };

    renderPreviewModal(matched, unrecognized);
    bootstrap.Modal.getOrCreateInstance(document.getElementById('import-preview-modal')).show();
}

function renderPreviewModal(matched, unrecognized) {
    const matchedBody  = document.getElementById('import-matched-body');
    const unrecSection = document.getElementById('import-unrecognized-section');
    const unrecList    = document.getElementById('import-unrecognized-list');
    const confirmBtn   = document.getElementById('import-confirm-btn');
    const statusEl     = document.getElementById('import-status');
    if (statusEl) { statusEl.textContent = ''; statusEl.classList.add('d-none'); }

    if (matched.length === 0) {
        matchedBody.innerHTML = '<tr><td colspan="3" class="text-muted text-center">No camps matched.</td></tr>';
        confirmBtn.disabled = true;
    } else {
        matchedBody.innerHTML = matched.map(m => `
            <tr>
                <td>${escHtml(m.campName)}</td>
                <td class="text-end">${formatArea(m.previousAreaSqm)}</td>
                <td class="text-end">${formatArea(m.newAreaSqm)}</td>
            </tr>
        `).join('');
        confirmBtn.disabled = false;
    }

    if (unrecognized.length > 0) {
        unrecList.innerHTML = unrecognized.map(n => `<li>${escHtml(n)}</li>`).join('');
        unrecSection.classList.remove('d-none');
    } else {
        unrecSection.classList.add('d-none');
    }
}

function escHtml(s) {
    const d = document.createElement('div');
    d.textContent = s;
    return d.innerHTML;
}

async function handleConfirm() {
    if (!pendingImport?.matched?.length) return;

    const confirmBtn  = document.getElementById('import-confirm-btn');
    const statusEl    = document.getElementById('import-status');
    const tokenEl = document.querySelector('input[name="__RequestVerificationToken"]');
    if (!tokenEl) { showError('Security token missing. Please refresh the page.'); return; }
    const token = tokenEl.value;
    const now         = new Date();
    const noteDate    = now.toISOString().slice(0, 16).replace('T', ' ');
    const note        = `Imported ${noteDate}`;

    confirmBtn.disabled = true;
    if (statusEl) {
        statusEl.textContent = `Updating ${pendingImport.matched.length} polygon(s)…`;
        statusEl.classList.remove('d-none');
    }

    let successCount = 0;
    const failures   = [];

    for (const item of pendingImport.matched) {
        const resp = await fetch(`/api/city-planning/camp-polygons/${item.campSeasonId}`, {
            method: 'PUT',
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': token,
            },
            body: JSON.stringify({ geoJson: item.geoJson, areaSqm: item.newAreaSqm, note }),
        });
        if (resp.ok) {
            successCount++;
        } else {
            failures.push(item.campName);
        }
    }

    bootstrap.Modal.getInstance(document.getElementById('import-preview-modal'))?.hide();

    const resultDiv = document.getElementById('import-result');
    if (resultDiv && failures.length === 0) {
        resultDiv.className = 'alert alert-success mt-2';
        resultDiv.textContent = `${successCount} polygon(s) updated.`;
    } else if (resultDiv) {
        resultDiv.className = 'alert alert-warning mt-2';
        resultDiv.textContent = `${successCount} updated, ${failures.length} failed: ${failures.join(', ')}.`;
    }
    resultDiv?.classList.remove('d-none');
    pendingImport = null;
    fileInput.value = '';
}

previewBtn?.addEventListener('click', handlePreview);
document.getElementById('import-confirm-btn')?.addEventListener('click', handleConfirm);
