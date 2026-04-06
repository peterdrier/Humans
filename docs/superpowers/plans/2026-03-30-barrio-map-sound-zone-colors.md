# Barrio Map: Sound Zone Color Coding — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Color each camp polygon on the Barrio Map by the camp's sound zone preference, with gray for no preference and a rainbow stripe pattern for Surprise.

**Architecture:** Add `SoundZone?` to `CampPolygonDto`, project it in `CampMapService`, pass it through the SignalR broadcast, then use MapLibre `match` expressions and a canvas-generated fill pattern in `Index.cshtml`.

**Tech Stack:** C# record, MapLibre GL JS `match` expression + `addImage`, HTML5 Canvas API, SignalR

---

## Files Modified

| File | Change |
|------|--------|
| `src/Humans.Application/Interfaces/ICampMapService.cs` | Add `SoundZone? SoundZone` to `CampPolygonDto`; add `GetCampSeasonSoundZoneAsync` to interface |
| `src/Humans.Infrastructure/Services/CampMapService.cs` | Project `SoundZone` in `GetPolygonsAsync`; implement `GetCampSeasonSoundZoneAsync` |
| `src/Humans.Web/Controllers/CampMapApiController.cs` | Add `soundZone` (int) to `PolygonUpdated` SignalR broadcast in `SavePolygon` and `RestorePolygon` |
| `src/Humans.Web/Views/BarrioMap/Index.cshtml` | Add `generateRainbowPattern()`, update `renderMap()` with color expressions and surprise layer, update SignalR handler |
| `tests/Humans.Application.Tests/Services/CampMapServiceTests.cs` | Add tests for `SoundZone` projection in `GetPolygonsAsync` |

---

## Task 1: Add SoundZone to CampPolygonDto and CampMapService

**Files:**
- Modify: `src/Humans.Application/Interfaces/ICampMapService.cs`
- Modify: `src/Humans.Infrastructure/Services/CampMapService.cs`
- Test: `tests/Humans.Application.Tests/Services/CampMapServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `CampMapServiceTests.cs`:

```csharp
[Fact]
public async Task GetPolygonsAsync_IncludesSoundZone_WhenSet()
{
    var (_, season, user) = await SeedCampWithLeadAsync();
    season.SoundZone = SoundZone.Blue;
    await _dbContext.SaveChangesAsync();
    const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]}}""";
    await _sut.SavePolygonAsync(season.Id, geoJson, 100.0, user.Id);

    var polygons = await _sut.GetPolygonsAsync(season.Year);

    polygons.Single().SoundZone.Should().Be(SoundZone.Blue);
}

[Fact]
public async Task GetPolygonsAsync_SoundZoneIsNull_WhenNotSet()
{
    var (_, season, user) = await SeedCampWithLeadAsync();
    // SoundZone not set, defaults to null
    const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]}}""";
    await _sut.SavePolygonAsync(season.Id, geoJson, 100.0, user.Id);

    var polygons = await _sut.GetPolygonsAsync(season.Year);

    polygons.Single().SoundZone.Should().BeNull();
}

[Fact]
public async Task GetCampSeasonSoundZoneAsync_ReturnsSoundZone_WhenSet()
{
    var (_, season, _) = await SeedCampWithLeadAsync();
    season.SoundZone = SoundZone.Red;
    await _dbContext.SaveChangesAsync();

    var result = await _sut.GetCampSeasonSoundZoneAsync(season.Id);

    result.Should().Be(SoundZone.Red);
}

[Fact]
public async Task GetCampSeasonSoundZoneAsync_ReturnsNull_WhenNotSet()
{
    var (_, season, _) = await SeedCampWithLeadAsync();

    var result = await _sut.GetCampSeasonSoundZoneAsync(season.Id);

    result.Should().BeNull();
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
cd /home/david/dev/nowhere/Humans
dotnet test tests/Humans.Application.Tests --filter "GetPolygonsAsync_IncludesSoundZone|GetPolygonsAsync_SoundZoneIsNull|GetCampSeasonSoundZoneAsync" 2>&1 | tail -20
```

Expected: build error — `CampPolygonDto` has no `SoundZone` property and `GetCampSeasonSoundZoneAsync` does not exist.

- [ ] **Step 3: Add SoundZone to CampPolygonDto and add interface method**

In `src/Humans.Application/Interfaces/ICampMapService.cs`:

Replace:
```csharp
public record CampPolygonDto(
    Guid CampSeasonId,
    string CampName,
    string CampSlug,
    string GeoJson,
    double AreaSqm);
```
With:
```csharp
public record CampPolygonDto(
    Guid CampSeasonId,
    string CampName,
    string CampSlug,
    string GeoJson,
    double AreaSqm,
    SoundZone? SoundZone);
```

Also add to the `ICampMapService` interface (after `GetPolygonHistoryAsync`):
```csharp
Task<SoundZone?> GetCampSeasonSoundZoneAsync(Guid campSeasonId, CancellationToken cancellationToken = default);
```

Add the missing using at the top if not already present:
```csharp
using Humans.Domain.Enums;
```

- [ ] **Step 4: Update GetPolygonsAsync projection and implement GetCampSeasonSoundZoneAsync**

In `src/Humans.Infrastructure/Services/CampMapService.cs`:

Replace the `Select` in `GetPolygonsAsync`:
```csharp
.Select(p => new CampPolygonDto(
    p.CampSeasonId,
    p.CampSeason.Name,
    p.CampSeason.Camp.Slug,
    p.GeoJson,
    p.AreaSqm,
    p.CampSeason.SoundZone))
```

Add after `GetPolygonsAsync`:
```csharp
public async Task<SoundZone?> GetCampSeasonSoundZoneAsync(Guid campSeasonId, CancellationToken cancellationToken = default)
{
    return await _dbContext.CampSeasons
        .Where(s => s.Id == campSeasonId)
        .Select(s => s.SoundZone)
        .FirstOrDefaultAsync(cancellationToken);
}
```

Add the missing using at the top if not already present:
```csharp
using Humans.Domain.Enums;
```

- [ ] **Step 5: Run the new tests**

```bash
dotnet test tests/Humans.Application.Tests --filter "GetPolygonsAsync_IncludesSoundZone|GetPolygonsAsync_SoundZoneIsNull|GetCampSeasonSoundZoneAsync" 2>&1 | tail -20
```

Expected: 4 tests pass.

- [ ] **Step 6: Build the full solution to confirm no other callers broke**

```bash
dotnet build Humans.slnx 2>&1 | grep -E "error|warning" | head -30
```

Expected: 0 errors.

- [ ] **Step 7: Run the full test suite**

```bash
dotnet test Humans.slnx 2>&1 | tail -10
```

Expected: all tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Application/Interfaces/ICampMapService.cs \
        src/Humans.Infrastructure/Services/CampMapService.cs \
        tests/Humans.Application.Tests/Services/CampMapServiceTests.cs
git commit -m "$(cat <<'EOF'
feat(barrio-map): add SoundZone to CampPolygonDto and service

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Pass soundZone through SignalR broadcast

**Files:**
- Modify: `src/Humans.Web/Controllers/CampMapApiController.cs`

- [ ] **Step 1: Update SavePolygon to broadcast soundZone**

In `CampMapApiController.cs`, replace the `SavePolygon` action body's broadcast line:

```csharp
// Before
await _hubContext.Clients.All.SendAsync(
    "PolygonUpdated", campSeasonId, polygon.GeoJson, polygon.AreaSqm, cancellationToken);
```

With:
```csharp
var soundZone = await _campMapService.GetCampSeasonSoundZoneAsync(campSeasonId, cancellationToken);
var soundZoneValue = soundZone.HasValue ? (int)soundZone.Value : -1;
await _hubContext.Clients.All.SendAsync(
    "PolygonUpdated", campSeasonId, polygon.GeoJson, polygon.AreaSqm, soundZoneValue, cancellationToken);
```

- [ ] **Step 2: Update RestorePolygon to broadcast soundZone**

In the same file, replace the `RestorePolygon` action's broadcast line:

```csharp
// Before
await _hubContext.Clients.All.SendAsync(
    "PolygonUpdated", campSeasonId, polygon.GeoJson, polygon.AreaSqm, cancellationToken);
```

With:
```csharp
var soundZone = await _campMapService.GetCampSeasonSoundZoneAsync(campSeasonId, cancellationToken);
var soundZoneValue = soundZone.HasValue ? (int)soundZone.Value : -1;
await _hubContext.Clients.All.SendAsync(
    "PolygonUpdated", campSeasonId, polygon.GeoJson, polygon.AreaSqm, soundZoneValue, cancellationToken);
```

- [ ] **Step 3: Build to confirm no errors**

```bash
dotnet build Humans.slnx 2>&1 | grep -E "error" | head -20
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/CampMapApiController.cs
git commit -m "$(cat <<'EOF'
feat(barrio-map): include soundZone in PolygonUpdated SignalR broadcast

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Frontend color rendering

**Files:**
- Modify: `src/Humans.Web/Views/BarrioMap/Index.cshtml`

- [ ] **Step 1: Add generateRainbowPattern() function**

Inside the `<script>` block, add this function before `init()`:

```js
function generateRainbowPattern() {
    const size = 60;
    const canvas = document.createElement('canvas');
    canvas.width = size;
    canvas.height = size;
    const ctx = canvas.getContext('2d');
    const colors = ['#ff0000', '#ff8800', '#ffcc00', '#33cc55', '#3388ff', '#cc00cc'];
    const stripeH = size / colors.length;
    for (let i = 0; i < colors.length; i++) {
        ctx.fillStyle = colors[i];
        ctx.fillRect(0, i * stripeH, size, stripeH);
    }
    return canvas;
}
```

- [ ] **Step 2: Register the rainbow pattern after map load**

In `init()`, after the `await new Promise(resolve => map.on('load', resolve));` line and after the `draw-label` source/layer setup, add:

```js
map.addImage('rainbow-pattern', generateRainbowPattern());
```

- [ ] **Step 3: Update renderMap() — layer cleanup**

In `renderMap()`, replace:
```js
['limit-zone-line', 'limit-zone-fill', 'polygons-outline', 'polygons-fill', 'polygons-labels'].forEach(id => {
```
With:
```js
['limit-zone-line', 'limit-zone-fill', 'polygons-outline', 'polygons-fill-surprise', 'polygons-fill', 'polygons-labels'].forEach(id => {
```

- [ ] **Step 4: Update feature property mapping to include soundZone**

In `renderMap()`, replace the `features` mapping:
```js
const features = state.polygons.map(p => {
    const f = JSON.parse(p.geoJson);
    f.properties = Object.assign(f.properties || {}, {
        campSeasonId: p.campSeasonId,
        campName: p.campName,
        areaSqm: p.areaSqm,
        isOwn: p.campSeasonId === USER_CAMP_SEASON_ID
    });
    return f;
});
```
With:
```js
const features = state.polygons.map(p => {
    const f = JSON.parse(p.geoJson);
    f.properties = Object.assign(f.properties || {}, {
        campSeasonId: p.campSeasonId,
        campName: p.campName,
        areaSqm: p.areaSqm,
        isOwn: p.campSeasonId === USER_CAMP_SEASON_ID,
        soundZone: (p.soundZone !== undefined && p.soundZone !== null) ? p.soundZone : -1
    });
    return f;
});
```

- [ ] **Step 5: Update polygons-fill layer with sound zone color match expression**

Replace the `polygons-fill` layer:
```js
map.addLayer({
    id: 'polygons-fill', type: 'fill', source: 'polygons',
    paint: {
        'fill-color': ['case', ['boolean', ['get', 'isOwn'], false], '#00bfff', '#00ffbf'],
        'fill-opacity': 0.35
    }
});
```
With:
```js
map.addLayer({
    id: 'polygons-fill', type: 'fill', source: 'polygons',
    filter: ['!=', ['get', 'soundZone'], 5],
    paint: {
        'fill-color': ['match', ['get', 'soundZone'],
            0, '#3388ff',
            1, '#33cc55',
            2, '#ffcc00',
            3, '#ff8800',
            4, '#ff3333',
            /* default (null/-1) */ '#999999'
        ],
        'fill-opacity': ['case', ['boolean', ['get', 'isOwn'], false], 0.55, 0.35]
    }
});
```

- [ ] **Step 6: Add polygons-fill-surprise layer for Surprise sound zone**

After the `polygons-fill` layer, add:
```js
map.addLayer({
    id: 'polygons-fill-surprise', type: 'fill', source: 'polygons',
    filter: ['==', ['get', 'soundZone'], 5],
    paint: {
        'fill-pattern': 'rainbow-pattern',
        'fill-opacity': ['case', ['boolean', ['get', 'isOwn'], false], 0.75, 0.55]
    }
});
```

- [ ] **Step 7: Update polygons-outline layer with sound zone border colors and own-camp width**

Replace the `polygons-outline` layer:
```js
map.addLayer({
    id: 'polygons-outline', type: 'line', source: 'polygons',
    paint: {
        'line-color': ['case', ['boolean', ['get', 'isOwn'], false], '#00bfff', '#00ffbf'],
        'line-width': 2
    }
});
```
With:
```js
map.addLayer({
    id: 'polygons-outline', type: 'line', source: 'polygons',
    paint: {
        'line-color': ['match', ['get', 'soundZone'],
            0, '#2266cc',
            1, '#229944',
            2, '#cc9900',
            3, '#cc6600',
            4, '#cc1111',
            5, '#cc00cc',
            /* default (null/-1) */ '#666666'
        ],
        'line-width': ['case', ['boolean', ['get', 'isOwn'], false], 4, 2]
    }
});
```

- [ ] **Step 8: Update SignalR PolygonUpdated handler to carry soundZone**

Replace:
```js
connection.on('PolygonUpdated', (campSeasonId, geoJson, areaSqm) => {
    const idx = state.polygons.findIndex(p => p.campSeasonId === campSeasonId);
    if (idx >= 0) {
        state.polygons[idx].geoJson = geoJson;
        state.polygons[idx].areaSqm = areaSqm;
    } else {
        state.polygons.push({ campSeasonId, geoJson, areaSqm, campName: '', campSlug: '' });
    }
```
With:
```js
connection.on('PolygonUpdated', (campSeasonId, geoJson, areaSqm, soundZone) => {
    const idx = state.polygons.findIndex(p => p.campSeasonId === campSeasonId);
    if (idx >= 0) {
        state.polygons[idx].geoJson = geoJson;
        state.polygons[idx].areaSqm = areaSqm;
        // soundZone is a CampSeason property — it doesn't change on polygon save
    } else {
        state.polygons.push({ campSeasonId, geoJson, areaSqm, soundZone: soundZone ?? -1, campName: '', campSlug: '' });
    }
```

- [ ] **Step 9: Update the SignalR setData features rebuild to include soundZone**

In the same `PolygonUpdated` handler, the `setData` call rebuilds features. Replace:
```js
const features = state.polygons.map(p => {
    const f = JSON.parse(p.geoJson);
    f.properties = Object.assign(f.properties || {}, {
        campSeasonId: p.campSeasonId,
        campName: p.campName,
        areaSqm: p.areaSqm,
        isOwn: p.campSeasonId === USER_CAMP_SEASON_ID
    });
    return f;
});
```
With:
```js
const features = state.polygons.map(p => {
    const f = JSON.parse(p.geoJson);
    f.properties = Object.assign(f.properties || {}, {
        campSeasonId: p.campSeasonId,
        campName: p.campName,
        areaSqm: p.areaSqm,
        isOwn: p.campSeasonId === USER_CAMP_SEASON_ID,
        soundZone: (p.soundZone !== undefined && p.soundZone !== null) ? p.soundZone : -1
    });
    return f;
});
```

- [ ] **Step 10: Build**

```bash
dotnet build Humans.slnx 2>&1 | grep -E "error" | head -20
```

Expected: 0 errors.

- [ ] **Step 11: Verify visually**

Run the app and open the Barrio Map. Confirm:
- Each polygon renders in the color matching its sound zone
- Surprise polygons show a rainbow stripe fill
- No-preference polygons are gray
- Your own camp has a thicker border (width 4) and higher opacity

```bash
dotnet run --project src/Humans.Web 2>&1 | head -5
```

- [ ] **Step 12: Commit**

```bash
git add src/Humans.Web/Views/BarrioMap/Index.cshtml
git commit -m "$(cat <<'EOF'
feat(barrio-map): color polygons by sound zone preference

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```
