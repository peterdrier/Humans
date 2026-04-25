# City Planning GeoJSON Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a GeoJSON polygon bulk-import card to the CityPlanning Admin page that lets map admins upload a file, preview matched/unmatched camps with before/after areas, confirm, and apply updates using the existing polygon save API.

**Architecture:** The backend change is minimal — add an optional `Note` field to `SaveCampPolygonRequest` so the JS can tag imports with `"Imported YYYY-MM-DD HH:mm"`. All matching, area computation (Turf.js), and sequenced save calls happen client-side in a new `admin-import.js` script loaded only on the Admin page. A Bootstrap modal provides the preview UX before any writes are committed.

**Tech Stack:** C# / ASP.NET Core MVC, Bootstrap 5 modal, Turf.js (already CDN-loaded on the map page; added to Admin page), vanilla JS ES module, CSRF via `RequestVerificationToken` header.

---

## Files

| Action | File |
|--------|------|
| Modify | `src/Humans.Application/Interfaces/CitiPlanning/ICityPlanningService.cs` |
| Modify | `src/Humans.Web/Controllers/CityPlanningApiController.cs` |
| Modify | `src/Humans.Web/Views/CityPlanning/Admin.cshtml` |
| Create | `src/Humans.Web/wwwroot/js/city-planning/admin-import.js` |

---

## Task 1: Create feature branch

- [ ] **Step 1: Create and switch to branch**

```bash
git checkout -b feat/city-planning-import
```

- [ ] **Step 2: Verify**

```bash
git branch --show-current
```

Expected output: `feat/city-planning-import`

---

## Task 2: Add optional `Note` to `SaveCampPolygonRequest`

The save endpoint currently ignores any note and always passes `"Saved"` to the service. This task exposes the `note` parameter that the service already supports.

**Files:**
- Modify: `src/Humans.Application/Interfaces/CitiPlanning/ICityPlanningService.cs:72`
- Modify: `src/Humans.Web/Controllers/CityPlanningApiController.cs:104-106`

- [ ] **Step 1: Add `Note` to the request record**

In `src/Humans.Application/Interfaces/CitiPlanning/ICityPlanningService.cs`, change line 72:

```csharp
// Before:
public record SaveCampPolygonRequest(string GeoJson, double AreaSqm);

// After:
public record SaveCampPolygonRequest(string GeoJson, double AreaSqm, string? Note = null);
```

- [ ] **Step 2: Pass the note through in the controller**

In `src/Humans.Web/Controllers/CityPlanningApiController.cs`, change the `SaveCampPolygon` action (around line 104):

```csharp
// Before:
var (polygon, _) = await _cityPlanningService.SaveCampPolygonAsync(
    campSeasonId, request.GeoJson, request.AreaSqm, userId,
    cancellationToken: cancellationToken);

// After:
var (polygon, _) = await _cityPlanningService.SaveCampPolygonAsync(
    campSeasonId, request.GeoJson, request.AreaSqm, userId,
    note: request.Note ?? "Saved",
    cancellationToken: cancellationToken);
```

- [ ] **Step 3: Build**

```bash
dotnet build Humans.slnx
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Interfaces/CitiPlanning/ICityPlanningService.cs \
        src/Humans.Web/Controllers/CityPlanningApiController.cs
git commit -m "feat(city-planning): add optional Note field to SaveCampPolygonRequest"
```

---

## Task 3: Create `admin-import.js`

This standalone script handles the full import flow: file reading, fetching map state, matching features to camps, area computation, preview dialog, and sequenced PUT calls.

**Files:**
- Create: `src/Humans.Web/wwwroot/js/city-planning/admin-import.js`

- [ ] **Step 1: Create the file with the matching and area computation logic**

```javascript
// admin-import.js — GeoJSON bulk import for the City Planning Admin page.
// Depends on: turf (global), Bootstrap 5 modal (global).

const fileInput   = document.getElementById('import-file-input');
const previewBtn  = document.getElementById('import-preview-btn');
const errorDiv    = document.getElementById('import-error');

let pendingImport = null; // { matched: [{campSeasonId, campName, previousAreaSqm, newAreaSqm, geoJson}], unrecognized: [string] }

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
    const lookup = new Map(); // key: normalised name or slug → {campSeasonId, campName, currentAreaSqm}
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
        if (seenIds.has(camp.campSeasonId)) continue; // skip duplicates in file
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
    const token       = document.querySelector('input[name="__RequestVerificationToken"]').value;
    const now         = new Date();
    const noteDate    = now.toISOString().slice(0, 16).replace('T', ' '); // "YYYY-MM-DD HH:mm" UTC
    const note        = `Imported ${noteDate}`;

    confirmBtn.disabled = true;
    statusEl.textContent = `Updating ${pendingImport.matched.length} polygon(s)…`;
    statusEl.classList.remove('d-none');

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
    if (failures.length === 0) {
        resultDiv.className = 'alert alert-success mt-2';
        resultDiv.textContent = `${successCount} polygon(s) updated.`;
    } else {
        resultDiv.className = 'alert alert-warning mt-2';
        resultDiv.textContent = `${successCount} updated, ${failures.length} failed: ${failures.join(', ')}.`;
    }
    resultDiv.classList.remove('d-none');
    pendingImport = null;
    fileInput.value = '';
}

previewBtn?.addEventListener('click', handlePreview);
document.getElementById('import-confirm-btn')?.addEventListener('click', handleConfirm);
```

- [ ] **Step 2: Commit**

```bash
git add src/Humans.Web/wwwroot/js/city-planning/admin-import.js
git commit -m "feat(city-planning): add admin-import.js for GeoJSON bulk polygon import"
```

---

## Task 4: Add Import card and modal to `Admin.cshtml`

**Files:**
- Modify: `src/Humans.Web/Views/CityPlanning/Admin.cshtml`

- [ ] **Step 1: Add the Import card after the existing Export card**

After the closing `</div>` of the Export card (line 188), add a new full-width column:

```html
        <!-- Import -->
        <div class="col-12">
            <div class="card">
                <div class="card-header fw-semibold">
                    <i class="fa-solid fa-file-import me-1"></i> Import polygons (@settings.Year)
                </div>
                <div class="card-body">
                    <p class="mb-2">Upload a GeoJSON FeatureCollection to bulk-update camp polygons for @settings.Year. Features are matched by <code>campName</code> or <code>campSlug</code> property.</p>
                    <div class="mb-3">
                        <label class="form-label small fw-semibold">GeoJSON file</label>
                        <input type="file" id="import-file-input" accept=".geojson,.json,application/geo+json" class="form-control form-control-sm" />
                    </div>
                    <button id="import-preview-btn" type="button" class="btn btn-primary btn-sm">
                        <i class="fa-solid fa-eye me-1"></i> Preview import
                    </button>
                    <div id="import-error" class="text-danger small mt-2 d-none"></div>
                    <div id="import-result" class="d-none mt-2"></div>
                </div>
            </div>
        </div>
```

- [ ] **Step 2: Add the Bootstrap preview modal**

Before the closing `</div>` of `<div class="container py-4">` (after all cards), add:

```html
<!-- Import preview modal -->
<div class="modal fade" id="import-preview-modal" tabindex="-1" aria-labelledby="import-preview-modal-label" aria-hidden="true">
    <div class="modal-dialog modal-lg">
        <div class="modal-content">
            <div class="modal-header">
                <h5 class="modal-title" id="import-preview-modal-label">
                    <i class="fa-solid fa-file-import me-1"></i> Import preview
                </h5>
                <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>
            </div>
            <div class="modal-body">
                <h6 class="mb-2">Will be updated</h6>
                <table class="table table-sm table-bordered mb-3">
                    <thead class="table-light">
                        <tr>
                            <th>Camp</th>
                            <th class="text-end">Previous area</th>
                            <th class="text-end">New area</th>
                        </tr>
                    </thead>
                    <tbody id="import-matched-body"></tbody>
                </table>

                <div id="import-unrecognized-section" class="d-none">
                    <h6 class="text-warning mb-1"><i class="fa-solid fa-triangle-exclamation me-1"></i> Unrecognized features (will be skipped)</h6>
                    <ul id="import-unrecognized-list" class="small text-muted mb-0"></ul>
                </div>

                <div id="import-status" class="text-muted small mt-3 d-none"></div>
            </div>
            <div class="modal-footer">
                <button type="button" class="btn btn-secondary btn-sm" data-bs-dismiss="modal">Cancel</button>
                <button type="button" id="import-confirm-btn" class="btn btn-primary btn-sm">
                    <i class="fa-solid fa-check me-1"></i> Confirm import
                </button>
            </div>
        </div>
    </div>
</div>
```

- [ ] **Step 3: Add Turf.js and the import script to the `@section Scripts` block**

At the bottom of `Admin.cshtml`, add (or create) a `@section Scripts` block:

```html
@section Scripts {
    <script src="https://unpkg.com/@@turf/turf@7.1.0/turf.min.js"></script>
    <script src="~/js/city-planning/admin-import.js"></script>
}
```

- [ ] **Step 4: Build**

```bash
dotnet build Humans.slnx
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Views/CityPlanning/Admin.cshtml
git commit -m "feat(city-planning): add GeoJSON import card and preview modal to Admin page"
```

---

## Task 5: Manual smoke test

- [ ] **Step 1: Run the app**

```bash
dotnet run --project src/Humans.Web
```

- [ ] **Step 2: Navigate to `/CityPlanning/Admin` as a map admin**

Verify the Import card appears below the Export card.

- [ ] **Step 3: Test with the exported file**

1. Download the current year's GeoJSON via the Export button.
2. Upload it to the Import card and click **Preview import**.
3. Verify the modal shows all camps with matching previous/new areas (they should be equal since it's a round-trip).
4. Click **Confirm import** and verify the success banner appears.
5. Navigate to `/CityPlanning` and open history for one of the updated camps — verify a `"Imported YYYY-MM-DD HH:mm"` entry appears, attributed to your user.

- [ ] **Step 4: Test with an unrecognized feature**

Manually edit the exported GeoJSON to rename one `campName` to something fake (e.g. `"Unknown Camp XYZ"`), then re-import. Verify the modal shows it in the "Unrecognized" list and the import still succeeds for the others.

- [ ] **Step 5: Test error states**

- Upload a plain text file → expect error banner "Invalid file — not valid JSON".
- Upload valid JSON that is not a FeatureCollection (e.g. `{}`) → expect error "File must be a GeoJSON FeatureCollection".

---

## Task 6: Update spec to note completed endpoint fix, then push

- [ ] **Step 1: Commit the spec fix (HTTP method correction already made)**

```bash
git add docs/superpowers/specs/2026-04-25-city-planning-import-design.md
git commit -m "docs: correct save endpoint to PUT /api/city-planning/camp-polygons/{id} in import spec"
```

- [ ] **Step 2: Push the branch**

```bash
git push -u origin feat/city-planning-import
```
