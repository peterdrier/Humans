# Barrio Map Official Zones Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow admins to upload a GeoJSON file of named official zones that are displayed as a read-only labelled overlay on the barrio map.

**Architecture:** Mirror the existing limit zone pattern exactly — add `OfficialZonesGeoJson` to `CampMapSettings`, expose upload/delete via admin routes, include in the state API, and render fill + outline + label layers in MapLibre using the `name` property of each GeoJSON Feature.

**Tech Stack:** ASP.NET Core MVC, EF Core, MapLibre GL JS.

---

## Files

| File | Action |
|------|--------|
| `src/Humans.Domain/Entities/CampMapSettings.cs` | Modify — add `OfficialZonesGeoJson` field |
| `src/Humans.Application/Interfaces/ICampMapService.cs` | Modify — add `UpdateOfficialZonesAsync`, `DeleteOfficialZonesAsync` |
| `src/Humans.Infrastructure/Services/CampMapService.cs` | Modify — implement both methods |
| `src/Humans.Infrastructure/Data/Migrations/<timestamp>_AddOfficialZonesToCampMapSettings.cs` | Create — EF migration |
| `src/Humans.Web/Controllers/CampMapApiController.cs` | Modify — add `officialZonesGeoJson` to state response |
| `src/Humans.Web/Controllers/BarrioMapController.cs` | Modify — add `UploadOfficialZones`, `DeleteOfficialZones` actions |
| `src/Humans.Web/Views/BarrioMap/Admin.cshtml` | Modify — add Official Zones card |
| `src/Humans.Web/wwwroot/js/barrio-map/layers.js` | Modify — render official zones layers with labels |
| `tests/Humans.Application.Tests/Services/CampMapServiceTests.cs` | Modify — add tests for both new service methods |

---

### Task 1: Domain entity + interface

**Files:**
- Modify: `src/Humans.Domain/Entities/CampMapSettings.cs`
- Modify: `src/Humans.Application/Interfaces/ICampMapService.cs`

- [ ] **Step 1: Add field to `CampMapSettings`**

In `src/Humans.Domain/Entities/CampMapSettings.cs`, add after `LimitZoneGeoJson`:

```csharp
/// <summary>GeoJSON FeatureCollection of official zones (read-only overlay). Each Feature must have a "name" property. Null until uploaded.</summary>
public string? OfficialZonesGeoJson { get; set; }
```

- [ ] **Step 2: Add methods to `ICampMapService`**

In `src/Humans.Application/Interfaces/ICampMapService.cs`, add after `DeleteLimitZoneAsync`:

```csharp
Task UpdateOfficialZonesAsync(string geoJson, Guid userId, CancellationToken cancellationToken = default);
Task DeleteOfficialZonesAsync(Guid userId, CancellationToken cancellationToken = default);
```

- [ ] **Step 3: Build to verify (expect one compile error — service not yet implemented)**

```bash
dotnet build Humans.slnx 2>&1 | grep -E "^.*error" | grep -v MSB3492
```

Expected: one `CS0535` error about `CampMapService` not implementing the two new methods. Domain + Application compile cleanly.

---

### Task 2: Service implementation + tests

**Files:**
- Modify: `src/Humans.Infrastructure/Services/CampMapService.cs`
- Modify: `tests/Humans.Application.Tests/Services/CampMapServiceTests.cs`

- [ ] **Step 1: Write failing tests**

In `CampMapServiceTests.cs`, add after the last test method:

```csharp
// --- UpdateOfficialZonesAsync / DeleteOfficialZonesAsync ---

[Fact]
public async Task UpdateOfficialZonesAsync_StoresGeoJson()
{
    await SeedMapSettingsAsync();
    const string geoJson = """{"type":"FeatureCollection","features":[]}""";

    await _sut.UpdateOfficialZonesAsync(geoJson, Guid.NewGuid());

    var settings = await _dbContext.CampMapSettings.SingleAsync();
    settings.OfficialZonesGeoJson.Should().Be(geoJson);
}

[Fact]
public async Task DeleteOfficialZonesAsync_SetsNull()
{
    await SeedMapSettingsAsync();
    var settings = await _dbContext.CampMapSettings.SingleAsync();
    settings.OfficialZonesGeoJson = """{"type":"FeatureCollection","features":[]}""";
    await _dbContext.SaveChangesAsync();

    await _sut.DeleteOfficialZonesAsync(Guid.NewGuid());

    var updated = await _dbContext.CampMapSettings.SingleAsync();
    updated.OfficialZonesGeoJson.Should().BeNull();
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Humans.Application.Tests --filter "OfficialZones" 2>&1 | tail -5
```

Expected: compile error — `UpdateOfficialZonesAsync` not implemented.

- [ ] **Step 3: Implement both methods in `CampMapService`**

In `src/Humans.Infrastructure/Services/CampMapService.cs`, add after `DeleteLimitZoneAsync`:

```csharp
public async Task UpdateOfficialZonesAsync(string geoJson, Guid userId, CancellationToken cancellationToken = default)
{
    var settings = await GetSettingsAsync(cancellationToken);
    settings.OfficialZonesGeoJson = geoJson;
    settings.UpdatedAt = _clock.GetCurrentInstant();
    await _dbContext.SaveChangesAsync(cancellationToken);
}

public async Task DeleteOfficialZonesAsync(Guid userId, CancellationToken cancellationToken = default)
{
    var settings = await GetSettingsAsync(cancellationToken);
    settings.OfficialZonesGeoJson = null;
    settings.UpdatedAt = _clock.GetCurrentInstant();
    await _dbContext.SaveChangesAsync(cancellationToken);
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Humans.Application.Tests --filter "OfficialZones" 2>&1 | tail -5
```

Expected: `Passed! - Failed: 0, Passed: 2`

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Domain/Entities/CampMapSettings.cs \
        src/Humans.Application/Interfaces/ICampMapService.cs \
        src/Humans.Infrastructure/Services/CampMapService.cs \
        tests/Humans.Application.Tests/Services/CampMapServiceTests.cs
git commit -m "feat: add official zones GeoJSON field and service methods"
```

---

### Task 3: EF migration

**Files:**
- Create: `src/Humans.Infrastructure/Data/Migrations/<timestamp>_AddOfficialZonesToCampMapSettings.cs`

- [ ] **Step 1: Generate migration**

```bash
export DOTNET_ROOT=/home/david/.dotnet && /home/david/.dotnet/tools/dotnet-ef migrations add AddOfficialZonesToCampMapSettings \
  --project src/Humans.Infrastructure \
  --startup-project src/Humans.Web
```

Expected: `Done. To undo this action, use 'ef migrations remove'`

- [ ] **Step 2: Verify migration content**

Open the generated file in `src/Humans.Infrastructure/Data/Migrations/`. It should contain one `AddColumn` call for `official_zones_geo_json` (or similar snake_case name), type `text`, nullable. No other changes.

- [ ] **Step 3: Run EF migration reviewer**

As per CLAUDE.md, run the EF migration reviewer agent (`.claude/agents/ef-migration-reviewer.md`) against the new migration file before committing.

- [ ] **Step 4: Commit migration**

```bash
git add src/Humans.Infrastructure/Data/Migrations/ src/Humans.Infrastructure/Migrations/HumansDbContextModelSnapshot.cs
git commit -m "feat: migration — add official_zones_geo_json to camp_map_settings"
```

---

### Task 4: State API + controller actions

**Files:**
- Modify: `src/Humans.Web/Controllers/CampMapApiController.cs`
- Modify: `src/Humans.Web/Controllers/BarrioMapController.cs`

- [ ] **Step 1: Add `officialZonesGeoJson` to state response**

In `src/Humans.Web/Controllers/CampMapApiController.cs`, update the `GetState` action's anonymous object:

```csharp
return Ok(new
{
    isPlacementOpen = settings.IsPlacementOpen,
    limitZoneGeoJson = settings.LimitZoneGeoJson,
    officialZonesGeoJson = settings.OfficialZonesGeoJson,
    campPolygons,
    campSeasonsWithoutPolygon = seasonsWithout
});
```

- [ ] **Step 2: Add `UploadOfficialZones` and `DeleteOfficialZones` actions to `BarrioMapController`**

In `src/Humans.Web/Controllers/BarrioMapController.cs`, add after `DeleteLimitZone`:

```csharp
[HttpPost("Admin/UploadOfficialZones")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> UploadOfficialZones(IFormFile file, CancellationToken cancellationToken)
{
    var userId = CurrentUserId();
    if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
        return Forbid();
    using var reader = new StreamReader(file.OpenReadStream());
    var geoJson = await reader.ReadToEndAsync(cancellationToken);
    await _campMapService.UpdateOfficialZonesAsync(geoJson, userId, cancellationToken);
    return RedirectToAction(nameof(Admin));
}

[HttpPost("Admin/DeleteOfficialZones")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> DeleteOfficialZones(CancellationToken cancellationToken)
{
    var userId = CurrentUserId();
    if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
        return Forbid();
    await _campMapService.DeleteOfficialZonesAsync(userId, cancellationToken);
    return RedirectToAction(nameof(Admin));
}
```

- [ ] **Step 3: Build to verify**

```bash
dotnet build Humans.slnx 2>&1 | grep -E "^.*error" | grep -v MSB3492
```

Expected: no output (no errors).

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/CampMapApiController.cs \
        src/Humans.Web/Controllers/BarrioMapController.cs
git commit -m "feat: expose official zones in state API and add admin upload/delete routes"
```

---

### Task 5: Admin view card

**Files:**
- Modify: `src/Humans.Web/Views/BarrioMap/Admin.cshtml`

- [ ] **Step 1: Add Official Zones card**

In `Admin.cshtml`, add a new card after the Limit Zone card (inside `<div class="row g-4">`):

```razor
<!-- Official Zones -->
<div class="col-md-6">
    <div class="card">
        <div class="card-header fw-semibold">
            <i class="fa fa-layer-group me-1"></i> Official Zones (@settings.Year)
        </div>
        <div class="card-body">
            @if (settings.OfficialZonesGeoJson != null)
            {
                <p class="text-success mb-3"><i class="fa fa-check-circle me-1"></i> Official zones uploaded</p>
                <form method="post" action="/BarrioMap/Admin/DeleteOfficialZones" class="d-inline">
                    @Html.AntiForgeryToken()
                    <button type="submit" class="btn btn-outline-danger btn-sm"
                            onclick="return confirm('Delete the official zones?')">
                        <i class="fa fa-trash me-1"></i> Remove
                    </button>
                </form>
            }
            else
            {
                <p class="text-muted mb-3">No official zones uploaded yet.</p>
            }
            <form method="post" action="/BarrioMap/Admin/UploadOfficialZones" enctype="multipart/form-data" class="mt-3">
                @Html.AntiForgeryToken()
                <div class="mb-2">
                    <label class="form-label small fw-semibold">Upload GeoJSON file</label>
                    <input type="file" name="file" accept=".geojson,.json" class="form-control form-control-sm" required />
                </div>
                <button type="submit" class="btn btn-primary btn-sm">
                    <i class="fa fa-upload me-1"></i> Upload
                </button>
            </form>
        </div>
    </div>
</div>
```

- [ ] **Step 2: Build to verify**

```bash
dotnet build Humans.slnx 2>&1 | grep -E "^.*error" | grep -v MSB3492
```

Expected: no output.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/BarrioMap/Admin.cshtml
git commit -m "feat: add official zones upload card to barrio map admin"
```

---

### Task 6: Frontend rendering

**Files:**
- Modify: `src/Humans.Web/wwwroot/js/barrio-map/layers.js`

- [ ] **Step 1: Add official zones layer IDs to the cleanup list**

In `renderMap`, update the layer/source cleanup arrays at the top to include the new IDs:

```js
['limit-zone-line', 'limit-zone-fill', 'official-zones-fill', 'official-zones-line', 'official-zones-labels', 'camp-polygons-fill-warning', 'camp-polygons-fill-overlap', 'camp-polygons-outline', 'camp-polygons-fill-surprise', 'camp-polygons-fill', 'camp-polygons-labels'].forEach(id => {
    if (map.getLayer(id)) map.removeLayer(id);
});
['limit-zone', 'official-zones', 'camp-polygons'].forEach(id => {
    if (map.getSource(id)) map.removeSource(id);
});
```

- [ ] **Step 2: Add official zones rendering after the limit zone block**

In `renderMap`, add after the `if (appState.campMap.limitZoneGeoJson)` block:

```js
if (appState.campMap.officialZonesGeoJson) {
    map.addSource('official-zones', { type: 'geojson', data: JSON.parse(appState.campMap.officialZonesGeoJson) });
    map.addLayer({ id: 'official-zones-fill', type: 'fill', source: 'official-zones', paint: { 'fill-color': '#ffdd44', 'fill-opacity': 0.12 } });
    map.addLayer({ id: 'official-zones-line', type: 'line', source: 'official-zones', paint: { 'line-color': '#ffdd44', 'line-width': 1.5 } });
    map.addLayer({
        id: 'official-zones-labels', type: 'symbol', source: 'official-zones',
        layout: {
            'text-field': ['get', 'name'],
            'text-size': 12,
            'text-anchor': 'center',
            'text-allow-overlap': false,
        },
        paint: { 'text-color': '#ffdd44', 'text-halo-color': '#000000', 'text-halo-width': 1.5 },
    });
}
```

- [ ] **Step 3: Build and run all tests**

```bash
dotnet build Humans.slnx 2>&1 | grep -E "^.*error" | grep -v MSB3492
dotnet test Humans.slnx 2>&1 | grep -E "^(Passed|Failed)"
```

Expected: no build errors, same pass/fail counts as before (2 pre-existing failures unrelated to this feature).

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/wwwroot/js/barrio-map/layers.js
git commit -m "feat: render official zones overlay with labels on barrio map"
```
