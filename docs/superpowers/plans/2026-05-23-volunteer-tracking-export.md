# Volunteer Tracking Export Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "Download XLSX" export to `/Shifts/Dashboard/VolunteerTracking` that produces a colored grid of humans × days — grouped by department, colored by the team a human worked most hours each day, with a white "arrival day" cell and a bottom totals row for meal counts.

**Architecture:** Clean Architecture across four projects. **Application** holds a new `IVolunteerTrackingExportService` (rules logic) + DTOs + a deterministic team-palette helper. **Infrastructure** adds one read-only repo method `GetConfirmedShiftsInRangeAsync` on the existing `IVolunteerTrackingRepository`. **Web** adds the `ExportXlsx` controller action, the ClosedXML-based `VolunteerTrackingXlsxBuilder`, an Export card on `Views/VolunteerTracking/Index.cshtml`, and a shared `ShiftFilterResolver` (lifted from `ShiftDashboardController.ResolveActiveDateRange`) so both controllers stay in sync. No new domain entities, no migrations.

**Tech Stack:** .NET 9, EF Core, Razor MVC, NodaTime (`LocalDate`/`Instant`), xUnit + NSubstitute (Application tests via `[HumansFact]`), `HumansWebApplicationFactory` (integration tests), **ClosedXML** (new — BSD-3-Clause) for XLSX writing.

**Spec:** [`docs/superpowers/specs/2026-05-23-volunteer-tracking-export-design.md`](../specs/2026-05-23-volunteer-tracking-export-design.md). Read it before starting Chunk 3 — that's where the rules get encoded.

---

## File Structure

**New files:**

| Path | Responsibility |
|---|---|
| `src/Humans.Application/DTOs/VolunteerTrackingExport/VolunteerExportRequest.cs` | Input record passed to the service. Filter params + resolved date range + eventSettingsId. |
| `src/Humans.Application/DTOs/VolunteerTrackingExport/VolunteerExportModel.cs` | Output model — `Days`, `Groups`, `Totals`, metadata. Builder consumes it. |
| `src/Humans.Application/DTOs/VolunteerTrackingExport/DepartmentGroup.cs` | One per dept: team identity + palette color + ordered humans. |
| `src/Humans.Application/DTOs/VolunteerTrackingExport/HumanRow.cs` | One per human: playa name + ordered `CellState` list. |
| `src/Humans.Application/DTOs/VolunteerTrackingExport/CellState.cs` | Discriminated cell: Empty / Arrival / Worked(teamId, color). |
| `src/Humans.Application/DTOs/VolunteerTrackingExport/ConfirmedShiftRow.cs` | Repo read-DTO — `(UserId, TeamId, StartsAtUtc, EndsAtUtc)`. |
| `src/Humans.Application/Services/Shifts/TeamPalette.cs` | Static helper — deterministic `Guid → hex color` via SHA256 + 20-color palette. |
| `src/Humans.Application/Interfaces/Shifts/IVolunteerTrackingExportService.cs` | Service contract — `BuildAsync(request, ct)`. |
| `src/Humans.Application/Services/Shifts/VolunteerTrackingExportService.cs` | Rules implementation — roster, grouping, arrival day, cell selection, totals. |
| `src/Humans.Web/Models/Shifts/ShiftFilterResolver.cs` | Shared static — `ResolveActiveDateRange(period, start, end)`. |
| `src/Humans.Web/Models/VolunteerTracking/VolunteerTrackingXlsxBuilder.cs` | Takes `VolunteerExportModel` → `(byte[], contentType, filename)` using ClosedXML. |
| `src/Humans.Web/Models/VolunteerTracking/VolunteerTrackingExportFormViewModel.cs` | Form state for the Export card (departments, selected filters). |
| `src/Humans.Web/Views/VolunteerTracking/_ExportCard.cshtml` | Partial view rendering the Export card + methodology blurb. |
| `tests/Humans.Application.Tests/Services/Shifts/TeamPaletteTests.cs` | Determinism + bounds tests. |
| `tests/Humans.Application.Tests/Services/Shifts/VolunteerTrackingExportServiceTests.cs` | Rules tests (the matrix from the spec). |
| `tests/Humans.Web.Tests/Models/Shifts/ShiftFilterResolverTests.cs` | Mutex behavior tests. |
| `tests/Humans.Web.Tests/Models/VolunteerTracking/VolunteerTrackingXlsxBuilderTests.cs` | Round-trip tests — write XLSX, read back, assert cells/fills/positions. |
| `tests/Humans.Integration.Tests/Repositories/Shifts/VolunteerTrackingRepositoryConfirmedShiftsTests.cs` | EF query test — confirmed-only, dept filter, range overlap. |

**Modified files:**

| Path | Change |
|---|---|
| `Directory.Packages.props` | Add `<PackageVersion Include="ClosedXML" Version="..." />`. |
| `src/Humans.Web/Humans.Web.csproj` | Add `<PackageReference Include="ClosedXML" />`. |
| `src/Humans.Application/Interfaces/Repositories/IVolunteerTrackingRepository.cs` | Add `GetConfirmedShiftsInRangeAsync(...)` signature. |
| `src/Humans.Infrastructure/Repositories/Shifts/VolunteerTrackingRepository.cs` | Implement `GetConfirmedShiftsInRangeAsync`. |
| `src/Humans.Web/Controllers/ShiftDashboardController.cs` | Replace internal `ResolveActiveDateRange` with call to `ShiftFilterResolver.Resolve`. |
| `src/Humans.Web/Controllers/VolunteerTrackingController.cs` | Add `[HttpGet("ExportXlsx")] ExportXlsx(...)` action. |
| `src/Humans.Web/Views/VolunteerTracking/Index.cshtml` | Render `_ExportCard` partial above the existing heatmap. |
| `src/Humans.Web/Extensions/Sections/ShiftsSectionExtensions.cs` | Register `IVolunteerTrackingExportService` and `VolunteerTrackingXlsxBuilder`. |
| `src/Humans.Web/Resources/SharedResource.*.resx` | Add the few new i18n keys for the Export card (English + Spanish placeholders). |

---

## Spec deviations (intentional)

| Spec says | Plan uses | Why |
|---|---|---|
| `Days: List<DateOnly>` (§Architecture) | `IReadOnlyList<LocalDate>` | NodaTime is the project's date type. Conversion happens at the EF boundary, not in DTOs. |
| `Cells: List<CellState>` | `IReadOnlyList<CellState>` | Project convention — read-only collections in DTOs. |
| `ConfirmedShiftRow = { UserId, TeamId, StartsAtUtc, EndsAtUtc }` | adds `TeamName` | Avoids a second team lookup in the service when building department groups; team rename is rare at this scale. |
| `VolunteerTrackingXlsxBuilder` (§Architecture) | same — `VolunteerTrackingXlsxBuilder.cs` | Aligned. (Earlier draft used `…XlsxExportBuilder`; renamed to match spec.) |

These deviations are deliberate alignment with project conventions. If a future reviewer thinks they're drift, point them at this table.

---

## Chunk 1: Foundation — ClosedXML, DTOs, Palette

This chunk adds the foundational pieces with no behavior change to the running app. After it, the codebase compiles and tests pass; the new pieces aren't wired up yet.

### Task 1.1: Add ClosedXML package

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/Humans.Web/Humans.Web.csproj`

- [ ] **Step 1: Pick the ClosedXML version**

Run: `dotnet package search ClosedXML --take 1 -v quiet`
Use the latest stable major (≥ 0.105 at time of writing). Record the chosen version.

- [ ] **Step 2: Add to centralized versions**

Edit `Directory.Packages.props`. In the `<ItemGroup>` block of `<PackageVersion>` entries (sorted alphabetically), add:

```xml
<PackageVersion Include="ClosedXML" Version="0.105.0" />
```

(Replace version with the one from Step 1.)

- [ ] **Step 3: Reference it from Humans.Web**

Edit `src/Humans.Web/Humans.Web.csproj`. In an existing `<ItemGroup>` containing `<PackageReference>` entries (or create one), add — alphabetically:

```xml
<PackageReference Include="ClosedXML" />
```

- [ ] **Step 4: Restore and build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds. No warnings related to ClosedXML.

- [ ] **Step 5: Commit**

```bash
git add Directory.Packages.props src/Humans.Web/Humans.Web.csproj
git commit -m "build: add ClosedXML package for XLSX export"
```

---

### Task 1.2: Create export DTOs

DTOs first — they nail down the contract between service and builder, and every later task depends on them.

**Files:**
- Create: `src/Humans.Application/DTOs/VolunteerTrackingExport/VolunteerExportRequest.cs`
- Create: `src/Humans.Application/DTOs/VolunteerTrackingExport/VolunteerExportModel.cs`
- Create: `src/Humans.Application/DTOs/VolunteerTrackingExport/DepartmentGroup.cs`
- Create: `src/Humans.Application/DTOs/VolunteerTrackingExport/HumanRow.cs`
- Create: `src/Humans.Application/DTOs/VolunteerTrackingExport/CellState.cs`
- Create: `src/Humans.Application/DTOs/VolunteerTrackingExport/ConfirmedShiftRow.cs`

- [ ] **Step 1: Create `VolunteerExportRequest.cs`**

```csharp
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Application.DTOs.VolunteerTrackingExport;

public sealed record VolunteerExportRequest(
    Guid EventSettingsId,
    Guid? DepartmentId,
    LocalDate StartDate,
    LocalDate EndDate,
    ShiftPeriod? Period,
    string ActorPlayaName,
    Instant GeneratedAtUtc);
```

- [ ] **Step 2: Create `CellState.cs`**

```csharp
namespace Humans.Application.DTOs.VolunteerTrackingExport;

public enum CellKind { Empty, Arrival, Worked }

public sealed record CellState(CellKind Kind, Guid? TeamId = null, string? TeamColorHex = null)
{
    public static CellState Empty { get; } = new(CellKind.Empty);
    public static CellState Arrival { get; } = new(CellKind.Arrival);
    public static CellState Worked(Guid teamId, string colorHex) => new(CellKind.Worked, teamId, colorHex);
}
```

- [ ] **Step 3: Create `HumanRow.cs`**

```csharp
namespace Humans.Application.DTOs.VolunteerTrackingExport;

public sealed record HumanRow(
    Guid UserId,
    string PlayaName,
    IReadOnlyList<CellState> Cells);
```

- [ ] **Step 4: Create `DepartmentGroup.cs`**

```csharp
namespace Humans.Application.DTOs.VolunteerTrackingExport;

public sealed record DepartmentGroup(
    Guid TeamId,
    string TeamName,
    string TeamColorHex,
    IReadOnlyList<HumanRow> Humans);
```

- [ ] **Step 5: Create `VolunteerExportModel.cs`**

```csharp
using NodaTime;

namespace Humans.Application.DTOs.VolunteerTrackingExport;

public sealed record VolunteerExportModel(
    string MethodologyBlurb,
    string FilterSummary,
    Instant GeneratedAtUtc,
    string GeneratedByName,
    IReadOnlyList<LocalDate> Days,
    IReadOnlyList<DepartmentGroup> Groups,
    IReadOnlyList<int> TotalsPerDay,
    string SuggestedFileName);
```

- [ ] **Step 6: Create `ConfirmedShiftRow.cs`**

```csharp
using NodaTime;

namespace Humans.Application.DTOs.VolunteerTrackingExport;

public sealed record ConfirmedShiftRow(
    Guid UserId,
    Guid TeamId,
    string TeamName,
    Instant StartsAtUtc,
    Instant EndsAtUtc);
```

(`TeamName` is included here so the service doesn't need a second lookup.)

- [ ] **Step 7: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: builds cleanly.

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Application/DTOs/VolunteerTrackingExport/
git commit -m "feat(shifts): add DTOs for volunteer tracking export"
```

---

### Task 1.3: Team palette helper (TDD)

A pure static utility — perfect for TDD. Determinism is the key property to lock in.

**Files:**
- Create: `tests/Humans.Application.Tests/Services/Shifts/TeamPaletteTests.cs`
- Create: `src/Humans.Application/Services/Shifts/TeamPalette.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Humans.Application.Tests/Services/Shifts/TeamPaletteTests.cs`:

```csharp
using FluentAssertions;
using Humans.Application.Services.Shifts;
using Humans.Tests.Common;

namespace Humans.Application.Tests.Services.Shifts;

public sealed class TeamPaletteTests
{
    [HumansFact]
    public void ColorFor_SameId_ReturnsSameColor()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        TeamPalette.ColorFor(id).Should().Be(TeamPalette.ColorFor(id));
    }

    [HumansFact]
    public void ColorFor_DifferentIds_OftenDifferent()
    {
        // Across 20 different Guids, we expect at least 5 distinct colors out of 20 palette entries.
        var colors = Enumerable.Range(0, 20)
            .Select(i => Guid.Parse($"{i:D8}-0000-0000-0000-000000000000"))
            .Select(TeamPalette.ColorFor)
            .Distinct()
            .Count();
        colors.Should().BeGreaterThan(5);
    }

    [HumansFact]
    public void ColorFor_ReturnsSixDigitHexWithLeadingHash()
    {
        var color = TeamPalette.ColorFor(Guid.NewGuid());
        color.Should().MatchRegex("^#[0-9A-Fa-f]{6}$");
    }

    [HumansFact]
    public void ColorFor_StabilityAcrossGuidFormatting_IsLocked()
    {
        // Spec locks Guid.ToString("D") — this test catches future drift.
        var id = Guid.Parse("abcd1234-5678-9abc-def0-123456789abc");
        TeamPalette.ColorFor(id).Should().Be("#" + ExpectedHexForGuidD(id));

        // Helper to make the lock explicit. If you change the hash algorithm,
        // update this expected value AND document why in the commit.
        static string ExpectedHexForGuidD(Guid id)
        {
            // Pre-computed expected color for this specific Guid + the documented palette.
            // Will be filled in after first run captures the actual value.
            return "PLACEHOLDER";
        }
    }
}
```

> **Note for the implementer:** the last test's `PLACEHOLDER` is intentional — run the test, capture the actual hex, paste it in, and commit. This converts the test from "asserts implementation" to "locks the deterministic mapping for one canonical input" so future palette changes show up clearly in a failing test.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --filter "FullyQualifiedName~TeamPaletteTests" -v quiet`
Expected: 4 failing tests (TeamPalette doesn't exist).

- [ ] **Step 3: Implement `TeamPalette.cs`**

Create `src/Humans.Application/Services/Shifts/TeamPalette.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Humans.Application.Services.Shifts;

public static class TeamPalette
{
    // 20 distinct hues, all dark enough that white bold text reads on top.
    // Order matters only for determinism — changing it shifts every team's color.
    private static readonly string[] Palette =
    [
        "#1F77B4", "#FF7F0E", "#2CA02C", "#D62728", "#9467BD",
        "#8C564B", "#E377C2", "#7F7F7F", "#BCBD22", "#17BECF",
        "#393B79", "#637939", "#8C6D31", "#843C39", "#7B4173",
        "#3182BD", "#E6550D", "#31A354", "#756BB1", "#636363",
    ];

    public static string ColorFor(Guid teamId)
    {
        // Guid.ToString("D") locked by spec — see 2026-05-23-volunteer-tracking-export-design.md
        var idString = teamId.ToString("D");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(idString));
        var index = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        return Palette[index % (uint)Palette.Length];
    }
}
```

- [ ] **Step 4: Run tests — three should pass, the placeholder still fails**

Run: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --filter "FullyQualifiedName~TeamPaletteTests" -v quiet`
Expected: 3 pass, 1 (placeholder) fails with a hex value in the message.

- [ ] **Step 5: Lock the placeholder**

Copy the actual hex value from the failure output. Replace `"PLACEHOLDER"` in `ColorFor_StabilityAcrossGuidFormatting_IsLocked` with that value (without the `#` — the assertion already adds it).

- [ ] **Step 6: Run all 4 tests, expect all pass**

Run: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --filter "FullyQualifiedName~TeamPaletteTests" -v quiet`
Expected: all 4 pass.

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Application/Services/Shifts/TeamPalette.cs tests/Humans.Application.Tests/Services/Shifts/TeamPaletteTests.cs
git commit -m "feat(shifts): deterministic team color palette for exports"
```

---

**End of Chunk 1.** Codebase compiles, ClosedXML is available, DTOs exist, palette is locked. No new behavior in the running app.

## Chunk 2: Repository — confirmed shifts in range

Adds the read-only EF query the service depends on. Tested as an integration test against a real `HumansDbContext` (matches the project's existing repo-test pattern).

### Task 2.1: Extend `IVolunteerTrackingRepository`

**Files:**
- Modify: `src/Humans.Application/Interfaces/Repositories/IVolunteerTrackingRepository.cs`

- [ ] **Step 1: Add the method signature**

At the bottom of the interface (just before the closing brace), add:

```csharp
/// <summary>
/// Returns confirmed shift signups whose [StartsAtUtc, EndsAtUtc) overlaps the date range
/// (in event-local time). When <paramref name="departmentId"/> is non-null, restricts to
/// shifts whose rota belongs to that team.
/// </summary>
Task<IReadOnlyList<ConfirmedShiftRow>> GetConfirmedShiftsInRangeAsync(
    Guid eventSettingsId,
    LocalDate startDate,
    LocalDate endDate,
    Guid? departmentId,
    CancellationToken ct);
```

Add the using at the top if not present:

```csharp
using Humans.Application.DTOs.VolunteerTrackingExport;
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: Application project builds. Infrastructure will FAIL because the implementation is missing — that's intentional, next task fixes it.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Interfaces/Repositories/IVolunteerTrackingRepository.cs
git commit -m "feat(shifts): add GetConfirmedShiftsInRangeAsync to repo interface"
```

---

### Task 2.2: Implement the EF query

**Files:**
- Modify: `src/Humans.Infrastructure/Repositories/Shifts/VolunteerTrackingRepository.cs`

- [ ] **Step 1: Locate the existing class**

Open the file and find the impl class. Note the existing constructor (it takes `HumansDbContext` or similar) and the existing private fields/services used (e.g., `IDateTimeZoneProvider`, event-time-zone resolver).

- [ ] **Step 2: Add the method**

Add this method to the class. Adjust the field names (`_db`, `_dbContext`, etc.) to match what the file already uses.

```csharp
public async Task<IReadOnlyList<ConfirmedShiftRow>> GetConfirmedShiftsInRangeAsync(
    Guid eventSettingsId,
    LocalDate startDate,
    LocalDate endDate,
    Guid? departmentId,
    CancellationToken ct)
{
    // Look up the event's time zone so we can clip date-range to UTC instants for the EF filter.
    var settings = await _db.EventSettings
        .AsNoTracking()
        .FirstOrDefaultAsync(e => e.Id == eventSettingsId, ct)
        ?? throw new InvalidOperationException($"EventSettings {eventSettingsId} not found.");

    var zone = DateTimeZoneProviders.Tzdb[settings.TimeZoneId];
    var rangeStartUtc = startDate.AtStartOfDayInZone(zone).ToInstant();
    var rangeEndUtcExclusive = endDate.PlusDays(1).AtStartOfDayInZone(zone).ToInstant();

    var query = _db.ShiftSignups
        .AsNoTracking()
        .Where(s => s.Status == ShiftSignupStatus.Confirmed)
        .Where(s => s.Shift.EventSettingsId == eventSettingsId)
        .Where(s => s.Shift.StartsAtUtc < rangeEndUtcExclusive
                 && s.Shift.EndsAtUtc > rangeStartUtc);

    if (departmentId.HasValue)
    {
        var deptId = departmentId.Value;
        query = query.Where(s => s.Shift.Rota.TeamId == deptId);
    }

    return await query
        .Select(s => new ConfirmedShiftRow(
            s.UserId,
            s.Shift.Rota.TeamId,
            s.Shift.Rota.Team.Name,
            s.Shift.StartsAtUtc,
            s.Shift.EndsAtUtc))
        .ToListAsync(ct);
}
```

> **Verify before continuing:** the exact navigation property names (`Shift.Rota.TeamId`, `Shift.Rota.Team.Name`, `Shift.EventSettingsId`, `Shift.StartsAtUtc`, `Shift.EndsAtUtc`, `ShiftSignupStatus.Confirmed`) match the entity model. Adjust if they differ — the shape of the projection must stay the same.

Add usings as needed at the top:

```csharp
using NodaTime;
using Humans.Application.DTOs.VolunteerTrackingExport;
```

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: full solution builds. If any navigation name was wrong, fix per the diagnostic and re-build.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Infrastructure/Repositories/Shifts/VolunteerTrackingRepository.cs
git commit -m "feat(shifts): implement GetConfirmedShiftsInRangeAsync"
```

---

### Task 2.3: Integration test for the query

Tests the EF query against a real DbContext. Mirrors the existing `VolunteerTrackingRepositoryTests` setup.

**Files:**
- Create: `tests/Humans.Integration.Tests/Repositories/Shifts/VolunteerTrackingRepositoryConfirmedShiftsTests.cs`

- [ ] **Step 1: Open the existing repository test for reference**

Open: `tests/Humans.Integration.Tests/Repositories/Shifts/VolunteerTrackingRepositoryTests.cs`

Identify:
- The `IClassFixture<HumansWebApplicationFactory>` pattern
- The scope-per-test pattern (`factory.Services.CreateAsyncScope()`)
- The `SeedActiveEventAsync(db)` helper or equivalent — what it returns, what it sets up

- [ ] **Step 2: Write the test class**

Create `tests/Humans.Integration.Tests/Repositories/Shifts/VolunteerTrackingRepositoryConfirmedShiftsTests.cs`:

```csharp
using FluentAssertions;
using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Persistence;
using Humans.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Repositories.Shifts;

public sealed class VolunteerTrackingRepositoryConfirmedShiftsTests
    : IClassFixture<HumansWebApplicationFactory>
{
    private readonly HumansWebApplicationFactory _factory;

    public VolunteerTrackingRepositoryConfirmedShiftsTests(HumansWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [HumansFact]
    public async Task ReturnsOnlyConfirmedSignups()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IVolunteerTrackingRepository>();

        var (eventId, _, _, _) = await SeedFixtureAsync(db);

        var start = new LocalDate(2026, 7, 7);
        var end = new LocalDate(2026, 7, 12);

        var rows = await repo.GetConfirmedShiftsInRangeAsync(eventId, start, end, departmentId: null, ct: default);

        rows.Should().OnlyContain(r => r.UserId != Guid.Empty);
        // The seed creates 2 Confirmed signups total (one TeamA, one TeamB), plus a Pending
        // and a Cancelled on TeamA shifts. Only the 2 Confirmed should appear.
        rows.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task ExcludesShiftsOutsideRange()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IVolunteerTrackingRepository>();

        var (eventId, _, _, _) = await SeedFixtureAsync(db);

        // Range entirely before the seeded shifts.
        var rows = await repo.GetConfirmedShiftsInRangeAsync(
            eventId,
            new LocalDate(2026, 1, 1),
            new LocalDate(2026, 1, 2),
            departmentId: null,
            ct: default);

        rows.Should().BeEmpty();
    }

    [HumansFact]
    public async Task DepartmentFilter_ExcludesOtherTeams()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IVolunteerTrackingRepository>();

        var (eventId, teamAId, teamBId, _) = await SeedFixtureAsync(db);

        var start = new LocalDate(2026, 7, 7);
        var end = new LocalDate(2026, 7, 12);

        var teamARows = await repo.GetConfirmedShiftsInRangeAsync(eventId, start, end, departmentId: teamAId, ct: default);
        var teamBRows = await repo.GetConfirmedShiftsInRangeAsync(eventId, start, end, departmentId: teamBId, ct: default);

        teamARows.Should().OnlyContain(r => r.TeamId == teamAId);
        teamBRows.Should().OnlyContain(r => r.TeamId == teamBId);
    }

    [HumansFact]
    public async Task ShiftThatOverlapsStartBoundary_IsIncluded()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IVolunteerTrackingRepository>();

        var (eventId, _, _, _) = await SeedFixtureAsync(db);

        // The seed includes a confirmed shift that starts on 2026-07-07 morning.
        // A range starting that same day must include it.
        var rows = await repo.GetConfirmedShiftsInRangeAsync(
            eventId,
            new LocalDate(2026, 7, 7),
            new LocalDate(2026, 7, 7),
            departmentId: null,
            ct: default);

        rows.Should().NotBeEmpty();
    }

    /// <summary>
    /// Seeds a minimal fixture:
    /// - One EventSettings ("Elsewhere 2026", Europe/Madrid).
    /// - Two teams: TeamA, TeamB. One rota per team.
    /// - Three shifts on 2026-07-07 (TeamA), 2026-07-08 (TeamB), 2026-07-09 (TeamA).
    /// - Three signups on the TeamA shifts: one Confirmed, one Pending, one Cancelled.
    /// - One Confirmed signup on the TeamB shift.
    /// </summary>
    private static async Task<(Guid eventId, Guid teamAId, Guid teamBId, Guid userId)> SeedFixtureAsync(HumansDbContext db)
    {
        // Implementation matches the project's existing seed helpers — use whatever
        // SeedActiveEventAsync / SeedTeamAsync / SeedShiftAsync utilities are already
        // present in tests/Humans.Integration.Tests/. Inline if they don't exist.
        // Returns IDs the tests need to assert against.

        throw new NotImplementedException("Implement using existing seed helpers — see VolunteerTrackingRepositoryTests for patterns.");
    }
}
```

> **Implementer note:** the `SeedFixtureAsync` body is left as a `throw` so the implementer doesn't blindly accept fake seed code. Read `VolunteerTrackingRepositoryTests.cs` and the project's seeding helpers, then fill in `SeedFixtureAsync` so the four tests have realistic, minimal data. If a more appropriate seed helper already exists, prefer it over inlining.

- [ ] **Step 3: Run the test class — expect compile-then-implement loop**

Run: `dotnet test tests/Humans.Integration.Tests/Humans.Integration.Tests.csproj --filter "FullyQualifiedName~VolunteerTrackingRepositoryConfirmedShiftsTests" -v quiet`
Expected: 4 tests, all fail with `NotImplementedException`.

- [ ] **Step 4: Implement `SeedFixtureAsync`**

Based on the project's existing seed pattern, create the minimal fixture described in the doc comment. Run the four tests; iterate until all pass.

Run: `dotnet test tests/Humans.Integration.Tests/Humans.Integration.Tests.csproj --filter "FullyQualifiedName~VolunteerTrackingRepositoryConfirmedShiftsTests" -v quiet`
Expected: all 4 pass.

- [ ] **Step 5: Commit**

```bash
git add tests/Humans.Integration.Tests/Repositories/Shifts/VolunteerTrackingRepositoryConfirmedShiftsTests.cs
git commit -m "test(shifts): integration tests for GetConfirmedShiftsInRangeAsync"
```

---

**End of Chunk 2.** Repository can fetch confirmed shifts filtered by event, date range, and optional department. No service layer or controller wiring yet.


## Chunk 3: Service — rules + grouping + totals

The big chunk: every rule from the spec gets encoded here. TDD throughout. Read the spec's §Roster Selection, §Row Grouping, §Cell Rules, §Totals Row, §Department-scoped Behavior before starting.

> **Implementer reminders before starting Chunk 3:**
> - `TeamPalette` lives in `Humans.Application.Services.Shifts` (Chunk 1, Task 1.3). When the service calls `TeamPalette.ColorFor(...)`, add `using Humans.Application.Services.Shifts;` at the top. No layer violation — both palette and service live in Application.
> - The test fixture uses a placeholder `UserInfo` constructor signature. Crack open `src/Humans.Application/UserInfo.cs` once at the start of Task 3.3 and reconcile the real record shape across ALL test construction sites in this chunk (Tasks 3.3, 3.4, 3.7, 3.10). Read `memory/architecture/burnername-is-the-display-name.md` for the BurnerName resolution rule the service relies on.
> - Banner rows (the colored merged row above each department's humans, per spec §Row Grouping) are XLSX-builder territory — synthesized in Chunk 4 from `DepartmentGroup`. The service emits `DepartmentGroup` records with `TeamColorHex`; it does NOT emit a synthetic banner `HumanRow`.

### Task 3.1: Lift `ResolveActiveDateRange` to `ShiftFilterResolver`

The export action needs the same period/date mutex `ShiftDashboardController` uses. Lift it to a shared static helper so both controllers call one implementation.

**Files:**
- Create: `src/Humans.Web/Models/Shifts/ShiftFilterResolver.cs`
- Create: `tests/Humans.Web.Tests/Models/Shifts/ShiftFilterResolverTests.cs`
- Modify: `src/Humans.Web/Controllers/ShiftDashboardController.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Humans.Web.Tests/Models/Shifts/ShiftFilterResolverTests.cs`:

```csharp
using FluentAssertions;
using Humans.Domain.Enums;
using Humans.Tests.Common;
using Humans.Web.Models.Shifts;
using NodaTime;

namespace Humans.Web.Tests.Models.Shifts;

public sealed class ShiftFilterResolverTests
{
    [HumansFact]
    public void Period_NotSet_DatesAreActive()
    {
        var (start, end) = ShiftFilterResolver.Resolve(
            period: null,
            filterStartDate: new LocalDate(2026, 7, 7),
            filterEndDate: new LocalDate(2026, 7, 12));

        start.Should().Be(new LocalDate(2026, 7, 7));
        end.Should().Be(new LocalDate(2026, 7, 12));
    }

    [HumansFact]
    public void Period_Set_DatesNulledOut()
    {
        var (start, end) = ShiftFilterResolver.Resolve(
            period: ShiftPeriod.Event,
            filterStartDate: new LocalDate(2026, 7, 7),
            filterEndDate: new LocalDate(2026, 7, 12));

        start.Should().BeNull();
        end.Should().BeNull();
    }

    [HumansFact]
    public void Nothing_Set_ReturnsNullNull()
    {
        var (start, end) = ShiftFilterResolver.Resolve(
            period: null,
            filterStartDate: null,
            filterEndDate: null);

        start.Should().BeNull();
        end.Should().BeNull();
    }

    [HumansFact]
    public void ResolvePeriodRange_Build_ReturnsBuildWindow()
    {
        // Gate opens 2026-07-09; BuildStartOffset = -7; EventEndOffset = 4; StrikeEndOffset = 6
        // Build = gate-7 .. gate-1
        var es = MakeEventSettings(gate: new LocalDate(2026, 7, 9), buildStart: -7, eventEnd: 4, strikeEnd: 6);
        var (from, to) = ShiftFilterResolver.ResolvePeriodRange(ShiftPeriod.Build, es);
        from.Should().Be(new LocalDate(2026, 7, 2));
        to.Should().Be(new LocalDate(2026, 7, 8));
    }

    [HumansFact]
    public void ResolvePeriodRange_Event_ReturnsEventWindow()
    {
        var es = MakeEventSettings(gate: new LocalDate(2026, 7, 9), buildStart: -7, eventEnd: 4, strikeEnd: 6);
        var (from, to) = ShiftFilterResolver.ResolvePeriodRange(ShiftPeriod.Event, es);
        from.Should().Be(new LocalDate(2026, 7, 9));
        to.Should().Be(new LocalDate(2026, 7, 13));
    }

    [HumansFact]
    public void ResolvePeriodRange_Strike_ReturnsStrikeWindow()
    {
        var es = MakeEventSettings(gate: new LocalDate(2026, 7, 9), buildStart: -7, eventEnd: 4, strikeEnd: 6);
        var (from, to) = ShiftFilterResolver.ResolvePeriodRange(ShiftPeriod.Strike, es);
        from.Should().Be(new LocalDate(2026, 7, 14));
        to.Should().Be(new LocalDate(2026, 7, 15));
    }

    // Test helper — adjust to project's EventSettings constructor / factory pattern.
    private static EventSettings MakeEventSettings(LocalDate gate, int buildStart, int eventEnd, int strikeEnd) =>
        throw new NotImplementedException(
            "Construct an EventSettings with GateOpeningDate=gate, BuildStartOffset=buildStart, " +
            "EventEndOffset=eventEnd, StrikeEndOffset=strikeEnd. Use the same construction approach the " +
            "existing ShiftBrowsePageBuilderPieSortTests / ShiftsControllerTests use — most likely an " +
            "object initializer with `new EventSettings { ... }`.");
}
```

> **Implementer note:** the `MakeEventSettings` helper is a deliberate `throw` — wire it up against the project's real `EventSettings` constructor pattern (see `ShiftBrowsePageBuilder.cs:221` for the exact field names used).

- [ ] **Step 2: Run tests — expect compile failure**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build fails — `ShiftFilterResolver` doesn't exist.

- [ ] **Step 3: Create `ShiftFilterResolver`**

Create `src/Humans.Web/Models/Shifts/ShiftFilterResolver.cs`:

```csharp
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models.Shifts;

/// <summary>
/// Server-side period↔date-range mutex (Shifts.md L237).
/// Dates filter only when period is null; once a preset period is selected,
/// explicit dates are ignored so the URL has one source of truth.
/// </summary>
public static class ShiftFilterResolver
{
    public static (LocalDate? activeStart, LocalDate? activeEnd) Resolve(
        ShiftPeriod? period, LocalDate? filterStartDate, LocalDate? filterEndDate)
    {
        var datesAreFilter = !period.HasValue && (filterStartDate.HasValue || filterEndDate.HasValue);
        return (
            datesAreFilter ? filterStartDate : null,
            datesAreFilter ? filterEndDate : null);
    }

    /// <summary>
    /// Maps a preset period to its concrete date range on a given event.
    /// Mirrors the duplicated switches in <c>ShiftBrowsePageBuilder.GetPeriodDateRange</c>
    /// and <c>ShiftsController.GetPeriodDateRange</c> (consolidating those into this single
    /// home is intentional — see CLAUDE.md DRY rule).
    /// </summary>
    public static (LocalDate From, LocalDate To) ResolvePeriodRange(ShiftPeriod period, EventSettings es) =>
        period switch
        {
            ShiftPeriod.Build => (
                es.GateOpeningDate.PlusDays(es.BuildStartOffset),
                es.GateOpeningDate.PlusDays(-1)),
            ShiftPeriod.Event => (
                es.GateOpeningDate,
                es.GateOpeningDate.PlusDays(es.EventEndOffset)),
            ShiftPeriod.Strike => (
                es.GateOpeningDate.PlusDays(es.EventEndOffset + 1),
                es.GateOpeningDate.PlusDays(es.StrikeEndOffset)),
            _ => (
                es.GateOpeningDate.PlusDays(es.BuildStartOffset),
                es.GateOpeningDate.PlusDays(es.StrikeEndOffset))
        };
}
```

Add usings at the top of the file:

```csharp
using Humans.Domain.Entities;
```

> **Implementer note — followup cleanup:** after this resolver lands, the duplicate switches in `ShiftBrowsePageBuilder.cs:221` and `ShiftsController.cs:452` should be replaced with calls to `ShiftFilterResolver.ResolvePeriodRange`. That's a follow-up commit, not a blocker for this feature. Track it as a TODO in the wrap-up section.

- [ ] **Step 4: Update `ShiftDashboardController` to call the shared helper**

Open `src/Humans.Web/Controllers/ShiftDashboardController.cs`. Find the existing internal `ResolveActiveDateRange` helper (around line 37–45). Delete the method body. Find the call site inside `Index()` (around line 64) and replace `ResolveActiveDateRange(period, filterStartDate, filterEndDate)` with `ShiftFilterResolver.Resolve(period, filterStartDate, filterEndDate)`.

Add the using at the top:

```csharp
using Humans.Web.Models.Shifts;
```

- [ ] **Step 5: Build and run all tests**

Run: `dotnet build Humans.slnx -v quiet && dotnet test Humans.slnx --filter "FullyQualifiedName~ShiftFilterResolver|FullyQualifiedName~ShiftDashboardController" -v quiet`
Expected: build succeeds, the 3 new resolver tests pass, no `ShiftDashboardController` tests regress.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Web/Models/Shifts/ShiftFilterResolver.cs \
        tests/Humans.Web.Tests/Models/Shifts/ShiftFilterResolverTests.cs \
        src/Humans.Web/Controllers/ShiftDashboardController.cs
git commit -m "refactor(shifts): lift ResolveActiveDateRange to shared ShiftFilterResolver"
```

---

### Task 3.2: Interface + scaffolding for `IVolunteerTrackingExportService`

**Files:**
- Create: `src/Humans.Application/Interfaces/Shifts/IVolunteerTrackingExportService.cs`
- Create: `src/Humans.Application/Services/Shifts/VolunteerTrackingExportService.cs`

- [ ] **Step 1: Create the interface**

```csharp
using Humans.Application.DTOs.VolunteerTrackingExport;

namespace Humans.Application.Interfaces.Shifts;

public interface IVolunteerTrackingExportService
{
    Task<VolunteerExportModel> BuildAsync(VolunteerExportRequest request, CancellationToken ct);
}
```

- [ ] **Step 2: Create the scaffolding impl**

```csharp
using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Shifts;

public sealed class VolunteerTrackingExportService(
    IVolunteerTrackingRepository repository,
    IShiftManagementService shiftManagementService,
    IUserService userService)
    : IVolunteerTrackingExportService
{
    public Task<VolunteerExportModel> BuildAsync(VolunteerExportRequest request, CancellationToken ct)
        => throw new NotImplementedException();
}
```

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: builds cleanly.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Interfaces/Shifts/IVolunteerTrackingExportService.cs \
        src/Humans.Application/Services/Shifts/VolunteerTrackingExportService.cs
git commit -m "feat(shifts): scaffold IVolunteerTrackingExportService"
```

---

### Task 3.3: Service test fixture + first test (empty range)

The service test class will grow across several tasks. This task lays down the fixture pattern and the simplest case.

**Files:**
- Create: `tests/Humans.Application.Tests/Services/Shifts/VolunteerTrackingExportServiceTests.cs`

- [ ] **Step 1: Write the test class skeleton + first test**

```csharp
using FluentAssertions;
using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Shifts;
using Humans.Application.UserInfo;
using Humans.Domain.Enums;
using Humans.Tests.Common;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Shifts;

public sealed class VolunteerTrackingExportServiceTests
{
    // Fixed test event: Elsewhere 2026 in Europe/Madrid, with stable IDs for assertions.
    private static readonly Guid EventId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TeamA   = Guid.Parse("11111111-0000-0000-0000-000000000000");
    private static readonly Guid TeamB   = Guid.Parse("22222222-0000-0000-0000-000000000000");
    private static readonly Guid TeamC   = Guid.Parse("33333333-0000-0000-0000-000000000000");

    private static readonly Instant TestNow = Instant.FromUtc(2026, 5, 23, 12, 0);
    private static readonly LocalDate Day1 = new(2026, 7, 7);
    private static readonly LocalDate Day7 = new(2026, 7, 13);

    private static VolunteerExportRequest BuildRequest(
        Guid? departmentId = null,
        LocalDate? start = null,
        LocalDate? end = null,
        ShiftPeriod? period = null) =>
        new(
            EventSettingsId: EventId,
            DepartmentId: departmentId,
            StartDate: start ?? Day1,
            EndDate: end ?? Day7,
            Period: period,
            ActorPlayaName: "TestActor",
            GeneratedAtUtc: TestNow);

    private static (IVolunteerTrackingRepository repo, IShiftManagementService shiftMgmt, IUserService users)
        BuildMocks(
            IReadOnlyList<ConfirmedShiftRow> shifts,
            IReadOnlyList<(Guid TeamId, string TeamName)>? departments = null,
            IReadOnlyDictionary<Guid, string>? playaNames = null)
    {
        var repo = Substitute.For<IVolunteerTrackingRepository>();
        repo.GetConfirmedShiftsInRangeAsync(
            Arg.Any<Guid>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(shifts);

        var shiftMgmt = Substitute.For<IShiftManagementService>();
        shiftMgmt.GetDepartmentsWithRotasAsync(EventId)
            .Returns(departments ?? Array.Empty<(Guid, string)>());

        var users = Substitute.For<IUserService>();
        if (playaNames is not null)
        {
            foreach (var (userId, name) in playaNames)
            {
                // Match whichever batch lookup the service uses; refine after first impl pass.
                var userInfo = new UserInfo(
                    Id: userId,
                    Email: $"{userId}@test.local",
                    DisplayName: name,
                    Profile: null,
                    Roles: Array.Empty<string>());
                users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(userInfo);
            }
        }

        return (repo, shiftMgmt, users);
    }

    [HumansFact]
    public async Task EmptyRange_ReturnsModelWithNoGroupsButFullMetadata()
    {
        var (repo, shiftMgmt, users) = BuildMocks(shifts: Array.Empty<ConfirmedShiftRow>());
        var sut = new VolunteerTrackingExportService(repo, shiftMgmt, users);

        var model = await sut.BuildAsync(BuildRequest(), ct: default);

        model.Groups.Should().BeEmpty();
        model.TotalsPerDay.Should().AllBeEquivalentTo(0);
        model.Days.Should().HaveCount(7);
        model.MethodologyBlurb.Should().NotBeNullOrWhiteSpace();
        model.FilterSummary.Should().NotBeNullOrWhiteSpace();
        model.GeneratedByName.Should().Be("TestActor");
        model.SuggestedFileName.Should().Be("volunteer-tracking-2026-07-07-to-2026-07-13.xlsx");
    }
}
```

> **Implementer note:** the `UserInfo` constructor parameters above are placeholders. After cracking open `src/Humans.Application/UserInfo.cs`, adjust to the real record shape. The shape of the test (one mock per user) shouldn't change.

- [ ] **Step 2: Run the test — expect fail with `NotImplementedException`**

Run: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --filter "FullyQualifiedName~VolunteerTrackingExportServiceTests" -v quiet`
Expected: 1 test, fails with `NotImplementedException`.

- [ ] **Step 3: Implement enough of `BuildAsync` to pass the empty-range test**

In `VolunteerTrackingExportService.cs`, replace the throwing body:

```csharp
public async Task<VolunteerExportModel> BuildAsync(VolunteerExportRequest request, CancellationToken ct)
{
    var days = EnumerateDays(request.StartDate, request.EndDate);
    var shifts = await repository.GetConfirmedShiftsInRangeAsync(
        request.EventSettingsId, request.StartDate, request.EndDate, request.DepartmentId, ct);

    // Group construction comes in later tasks. For now: empty groups, zero totals.
    var groups = Array.Empty<DepartmentGroup>();
    var totals = new int[days.Count];

    var deptName = request.DepartmentId.HasValue ? "(filtered)" : "All";
    var periodLabel = request.Period?.ToString() ?? "custom";

    return new VolunteerExportModel(
        MethodologyBlurb: BuildMethodologyBlurb(),
        FilterSummary: $"Department: {deptName} · Range: {request.StartDate} → {request.EndDate} ({periodLabel})",
        GeneratedAtUtc: request.GeneratedAtUtc,
        GeneratedByName: request.ActorPlayaName,
        Days: days,
        Groups: groups,
        TotalsPerDay: totals,
        SuggestedFileName: BuildFileName(request, deptName));
}

private static IReadOnlyList<LocalDate> EnumerateDays(LocalDate start, LocalDate end)
{
    var count = Period.DaysBetween(start, end) + 1;
    var days = new LocalDate[count];
    for (var i = 0; i < count; i++) days[i] = start.PlusDays(i);
    return days;
}

private static string BuildMethodologyBlurb() =>
    "Rows = humans with ≥1 confirmed shift in range. Cell color = the team they worked most " +
    "hours that day. White cell = day before their first confirmed shift (arrival day). " +
    "Totals row = humans on-site that day (used for meal counts). Names shown are playa names.";

private static string BuildFileName(VolunteerExportRequest req, string? departmentSlug)
{
    var prefix = departmentSlug is { Length: > 0 } slug
        ? $"volunteer-tracking-{slug}-"
        : "volunteer-tracking-";
    return $"{prefix}{req.StartDate:yyyy-MM-dd}-to-{req.EndDate:yyyy-MM-dd}.xlsx";
}

private static string SlugifyTeamName(string teamName)
{
    // Spec §File Output slugification rule:
    //   1) lowercase, 2) strip diacritics (NFD + drop combining marks),
    //   3) non-[a-z0-9] -> '-', 4) collapse repeats, 5) trim '-', 6) fall back to "team".
    var lower = teamName.ToLowerInvariant();
    var nfd = lower.Normalize(System.Text.NormalizationForm.FormD);
    var sb = new System.Text.StringBuilder(nfd.Length);
    foreach (var ch in nfd)
    {
        var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
        if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
        sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
    }
    var collapsed = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
    return collapsed.Length > 0 ? collapsed : "team";
}
```

Add usings: `using NodaTime; using Humans.Domain.Enums;`.

- [ ] **Step 4: Run — expect pass**

Run: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --filter "FullyQualifiedName~VolunteerTrackingExportServiceTests" -v quiet`
Expected: 1 test passes.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Services/Shifts/VolunteerTrackingExportService.cs \
        tests/Humans.Application.Tests/Services/Shifts/VolunteerTrackingExportServiceTests.cs
git commit -m "feat(shifts): export service handles empty-range case"
```

---

### Task 3.4: One human, one team, three confirmed shifts

Adds: grouping by team, alphabetical humans, cells colored, arrival day white, totals row populated.

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/Shifts/VolunteerTrackingExportServiceTests.cs`
- Modify: `src/Humans.Application/Services/Shifts/VolunteerTrackingExportService.cs`

- [ ] **Step 1: Add the failing test**

Append inside the test class:

```csharp
private static readonly Guid Alice = Guid.Parse("a0000000-0000-0000-0000-000000000001");

[HumansFact]
public async Task SingleHuman_ThreeConsecutiveShifts_SingleTeam()
{
    // Alice has confirmed TeamA shifts on Day3, Day4, Day5 (in event-local).
    // Each is an 8-hour shift in Europe/Madrid; UTC = local - 2h.
    var shifts = new[]
    {
        ShiftRow(Alice, TeamA, "TeamA", Day1.PlusDays(2), 9, 17),
        ShiftRow(Alice, TeamA, "TeamA", Day1.PlusDays(3), 9, 17),
        ShiftRow(Alice, TeamA, "TeamA", Day1.PlusDays(4), 9, 17),
    };
    var (repo, shiftMgmt, users) = BuildMocks(
        shifts: shifts,
        departments: new[] { (TeamA, "TeamA") },
        playaNames: new Dictionary<Guid, string> { [Alice] = "Alice" });
    var sut = new VolunteerTrackingExportService(repo, shiftMgmt, users);

    var model = await sut.BuildAsync(BuildRequest(), ct: default);

    model.Groups.Should().HaveCount(1);
    var group = model.Groups[0];
    group.TeamId.Should().Be(TeamA);
    group.TeamName.Should().Be("TeamA");
    group.Humans.Should().HaveCount(1);

    var row = group.Humans[0];
    row.PlayaName.Should().Be("Alice");
    row.Cells.Should().HaveCount(7);
    // Day 0 = before arrival (Alice's first shift is Day3 → arrival = Day2).
    row.Cells[0].Kind.Should().Be(CellKind.Empty);
    // Day 1 (=Day2 from Day1+1, index 1) is one day before her first shift → arrival = white.
    row.Cells[1].Kind.Should().Be(CellKind.Arrival);
    // Day 2 (=Day3, index 2) — first shift — worked TeamA.
    row.Cells[2].Kind.Should().Be(CellKind.Worked);
    row.Cells[2].TeamId.Should().Be(TeamA);
    row.Cells[3].Kind.Should().Be(CellKind.Worked);
    row.Cells[4].Kind.Should().Be(CellKind.Worked);
    row.Cells[5].Kind.Should().Be(CellKind.Empty); // no shift Day6
    row.Cells[6].Kind.Should().Be(CellKind.Empty); // no shift Day7

    // Totals: 1 on Day3-5, 0 elsewhere (presence = has shift that day per spec).
    model.TotalsPerDay.Should().Equal(0, 0, 1, 1, 1, 0, 0);
}

/// <summary>Helper: build a ConfirmedShiftRow with start/end specified as event-local hours on a given local date.</summary>
private static ConfirmedShiftRow ShiftRow(Guid userId, Guid teamId, string teamName, LocalDate localDate, int startHourLocal, int endHourLocal)
{
    // Spec §Cell Rules: hours = clipped overlap with the day in event-local time.
    // Tests use Europe/Madrid (UTC+2 in July). The service is event-tz agnostic — it
    // receives Instants and must clip them per the event's zone. For test simplicity we
    // pre-compute the equivalent UTC instants here.
    var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
    var startInstant = (localDate + LocalTime.FromHourMinuteSecondTick(startHourLocal, 0, 0, 0)).InZoneStrictly(zone).ToInstant();
    var endInstant = (localDate + LocalTime.FromHourMinuteSecondTick(endHourLocal, 0, 0, 0)).InZoneStrictly(zone).ToInstant();
    return new ConfirmedShiftRow(userId, teamId, teamName, startInstant, endInstant);
}
```

> **Implementer note:** the test relies on the service knowing the event's time zone. Two ways to wire that: (1) the service injects something that returns the event's zone for a given `EventSettingsId`; or (2) the request DTO carries the zone (push the lookup into the controller). The plan picks (1) — see Step 2 — because it keeps the controller dumb. If you pick (2), update `VolunteerExportRequest` to add `IanaTimeZoneId` and have the controller fetch it.

- [ ] **Step 2: Implement the rules to pass the test**

Update `VolunteerTrackingExportService.cs`. Inject `IShiftManagementService` (already injected) — assume it has or add `GetEventTimeZoneIdAsync(Guid eventSettingsId)`. If that doesn't exist, add a separate small service or extend the repo. Verify the right home before adding.

Full implementation outline (filling in the empty-range branch):

```csharp
public async Task<VolunteerExportModel> BuildAsync(VolunteerExportRequest request, CancellationToken ct)
{
    var days = EnumerateDays(request.StartDate, request.EndDate);
    var shifts = await repository.GetConfirmedShiftsInRangeAsync(
        request.EventSettingsId, request.StartDate, request.EndDate, request.DepartmentId, ct);

    // Resolve the filtered team name (if filtered) for the filename + summary.
    string? filteredTeamName = null;
    if (request.DepartmentId is Guid deptId)
    {
        var depts = await shiftManagementService.GetDepartmentsWithRotasAsync(request.EventSettingsId);
        filteredTeamName = depts.FirstOrDefault(d => d.TeamId == deptId).TeamName;
    }

    if (shifts.Count == 0)
        return BuildEmptyModel(request, days, filteredTeamName);

    var zoneId = await shiftManagementService.GetEventTimeZoneIdAsync(request.EventSettingsId, ct);
    var zone = DateTimeZoneProviders.Tzdb[zoneId];

    // (1) Build (userId, day) → list of (teamId, teamName, hours) for the range.
    var perUserPerDay = BucketByUserDayTeam(shifts, days, zone);

    // (2) Per user: primary team = team with most total hours; first-shift date.
    var userIds = perUserPerDay.Keys.Select(k => k.userId).Distinct().ToList();
    var playaNames = await LoadPlayaNamesAsync(userIds, ct);
    var firstShiftDay = ComputeFirstShiftDay(shifts, zone);
    var primaryTeam = ComputePrimaryTeam(perUserPerDay);

    // (3) Build cells per user.
    var rows = new Dictionary<Guid, HumanRow>();
    foreach (var userId in userIds)
    {
        var cells = new CellState[days.Count];
        var arrivalDay = firstShiftDay[userId].PlusDays(-1);
        for (var i = 0; i < days.Count; i++)
        {
            var d = days[i];
            if (perUserPerDay.TryGetValue((userId, d), out var teamsThatDay))
            {
                var winner = teamsThatDay
                    .GroupBy(t => (t.teamId, t.teamName))
                    .Select(g => (g.Key.teamId, g.Key.teamName, hours: g.Sum(x => x.hours)))
                    .OrderByDescending(t => t.hours)
                    .ThenBy(t => t.teamName, StringComparer.OrdinalIgnoreCase)
                    .First();
                cells[i] = CellState.Worked(winner.teamId, TeamPalette.ColorFor(winner.teamId));
            }
            else if (d == arrivalDay)
            {
                cells[i] = CellState.Arrival;
            }
            else
            {
                cells[i] = CellState.Empty;
            }
        }
        rows[userId] = new HumanRow(userId, playaNames[userId], cells);
    }

    // (4) Group by primary team. Order groups by total team hours desc, tie-break team name.
    var groups = rows
        .GroupBy(r => primaryTeam[r.Key])
        .Select(g =>
        {
            var teamHumans = g.Select(r => r.Value)
                .OrderBy(h => h.PlayaName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var teamName = perUserPerDay
                .Where(kvp => kvp.Key.userId == g.Key.userId)  // placeholder — see fix below
                .SelectMany(kvp => kvp.Value).First().teamName;
            // The above is wrong — extract teamName via primaryTeam[] lookup instead. Cleaned up in 3.10.
            return new DepartmentGroup(g.Key.teamId, "TODO-team-name", TeamPalette.ColorFor(g.Key.teamId), teamHumans);
        })
        .ToList();

    var totals = ComputeTotals(days, rows, firstShiftDay);

    return BuildModel(request, days, groups, totals, filteredTeamName);
}
```

> **Implementer note:** the implementation above is the *shape* but is intentionally rough — the team-name lookup in step (4) is broken and Task 3.10 cleans it. For now, just get the test passing. If you can write a cleaner version that already passes the later tests, great — but don't skip ahead without writing those tests first (3.5 onwards).

- [ ] **Step 3: Add helper methods (private statics)**

```csharp
private static Dictionary<(Guid userId, LocalDate day), List<(Guid teamId, string teamName, double hours)>>
    BucketByUserDayTeam(IReadOnlyList<ConfirmedShiftRow> shifts, IReadOnlyList<LocalDate> days, DateTimeZone zone)
{
    var result = new Dictionary<(Guid, LocalDate), List<(Guid, string, double)>>();
    var rangeStart = days[0];
    var rangeEnd = days[^1];
    foreach (var s in shifts)
    {
        var localStart = s.StartsAtUtc.InZone(zone).LocalDateTime;
        var localEnd = s.EndsAtUtc.InZone(zone).LocalDateTime;
        var firstDay = LocalDate.Max(localStart.Date, rangeStart);
        var lastDay = LocalDate.Min(localEnd.Date, rangeEnd);
        for (var d = firstDay; d <= lastDay; d = d.PlusDays(1))
        {
            var dayStart = d.AtStartOfDayInZone(zone).LocalDateTime;
            var dayEnd = d.PlusDays(1).AtStartOfDayInZone(zone).LocalDateTime;
            var overlapStart = LocalDateTime.Max(dayStart, localStart);
            var overlapEnd = LocalDateTime.Min(dayEnd, localEnd);
            var hours = (overlapEnd - overlapStart).ToDuration().TotalHours;
            if (hours <= 0) continue;
            var key = (s.UserId, d);
            if (!result.TryGetValue(key, out var list))
                result[key] = list = new();
            list.Add((s.TeamId, s.TeamName, hours));
        }
    }
    return result;
}

private static Dictionary<Guid, LocalDate> ComputeFirstShiftDay(IReadOnlyList<ConfirmedShiftRow> shifts, DateTimeZone zone)
{
    var firstDay = new Dictionary<Guid, LocalDate>();
    foreach (var s in shifts)
    {
        var localStart = s.StartsAtUtc.InZone(zone).LocalDateTime.Date;
        if (!firstDay.TryGetValue(s.UserId, out var existing) || localStart < existing)
            firstDay[s.UserId] = localStart;
    }
    return firstDay;
}

private static Dictionary<Guid, (Guid teamId, string teamName)> ComputePrimaryTeam(
    Dictionary<(Guid userId, LocalDate day), List<(Guid teamId, string teamName, double hours)>> bucket)
{
    return bucket
        .SelectMany(kvp => kvp.Value.Select(v => (kvp.Key.userId, v.teamId, v.teamName, v.hours)))
        .GroupBy(t => (t.userId, t.teamId, t.teamName))
        .Select(g => (g.Key.userId, g.Key.teamId, g.Key.teamName, hours: g.Sum(x => x.hours)))
        .GroupBy(t => t.userId)
        .ToDictionary(
            g => g.Key,
            g => g.OrderByDescending(t => t.hours)
                  .ThenBy(t => t.teamName, StringComparer.OrdinalIgnoreCase)
                  .ThenBy(t => t.teamId)
                  .Select(t => (t.teamId, t.teamName))
                  .First());
}

// Intentionally sequential — at ~500 humans (CLAUDE.md scale guidance), the round-trip
// cost is negligible and sequential code is easier to debug than parallel awaits.
private async Task<Dictionary<Guid, string>> LoadPlayaNamesAsync(IReadOnlyList<Guid> userIds, CancellationToken ct)
{
    var result = new Dictionary<Guid, string>();
    foreach (var id in userIds)
    {
        var info = await userService.GetUserInfoAsync(id, ct);
        result[id] = info?.BurnerName ?? "(unknown)";
    }
    return result;
}

private static int[] ComputeTotals(
    IReadOnlyList<LocalDate> days,
    Dictionary<Guid, HumanRow> rows,
    Dictionary<Guid, LocalDate> firstShiftDay)
{
    var totals = new int[days.Count];
    for (var i = 0; i < days.Count; i++)
    {
        var d = days[i];
        var count = 0;
        foreach (var (userId, row) in rows)
        {
            if (firstShiftDay[userId] > d) continue;        // hasn't arrived yet
            if (row.Cells[i].Kind == CellKind.Worked) count++;
        }
        totals[i] = count;
    }
    return totals;
}

private static VolunteerExportModel BuildEmptyModel(VolunteerExportRequest request, IReadOnlyList<LocalDate> days, string? filteredTeamName)
{
    return BuildModel(request, days, Array.Empty<DepartmentGroup>(), new int[days.Count], filteredTeamName);
}

private static VolunteerExportModel BuildModel(
    VolunteerExportRequest request,
    IReadOnlyList<LocalDate> days,
    IReadOnlyList<DepartmentGroup> groups,
    IReadOnlyList<int> totals,
    string? filteredTeamName)
{
    var deptName = filteredTeamName ?? "All";
    var periodLabel = request.Period?.ToString() ?? "custom";
    var slug = filteredTeamName is null ? null : SlugifyTeamName(filteredTeamName);
    return new VolunteerExportModel(
        MethodologyBlurb: BuildMethodologyBlurb(),
        FilterSummary: $"Department: {deptName} · Range: {request.StartDate} → {request.EndDate} ({periodLabel})",
        GeneratedAtUtc: request.GeneratedAtUtc,
        GeneratedByName: request.ActorPlayaName,
        Days: days,
        Groups: groups,
        TotalsPerDay: totals,
        SuggestedFileName: BuildFileName(request, slug));
}
```

- [ ] **Step 4: Run all service tests — expect 2 passing**

Run: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --filter "FullyQualifiedName~VolunteerTrackingExportServiceTests" -v quiet`
Expected: 2 pass. If the second fails on team name being `"TODO-team-name"`, fix the lookup now (use `primaryTeam[g.Key.userId]` to get the team name).

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Services/Shifts/VolunteerTrackingExportService.cs \
        tests/Humans.Application.Tests/Services/Shifts/VolunteerTrackingExportServiceTests.cs
git commit -m "feat(shifts): single-human single-team grouping + arrival day + totals"
```

---

### Task 3.5: Multi-team day — max hours wins

- [ ] **Step 1: Append the failing test**

```csharp
[HumansFact]
public async Task MultiTeamDay_CellColoredByMaxHoursTeam()
{
    // Alice on Day3: TeamA 3h + TeamB 5h → cell = TeamB color.
    var shifts = new[]
    {
        ShiftRow(Alice, TeamA, "TeamA", Day1.PlusDays(2), 9, 12),
        ShiftRow(Alice, TeamB, "TeamB", Day1.PlusDays(2), 13, 18),
    };
    var (repo, shiftMgmt, users) = BuildMocks(
        shifts: shifts,
        departments: new[] { (TeamA, "TeamA"), (TeamB, "TeamB") },
        playaNames: new Dictionary<Guid, string> { [Alice] = "Alice" });
    var sut = new VolunteerTrackingExportService(repo, shiftMgmt, users);

    var model = await sut.BuildAsync(BuildRequest(), ct: default);

    var cellsDay3 = model.Groups.SelectMany(g => g.Humans).First().Cells[2];
    cellsDay3.Kind.Should().Be(CellKind.Worked);
    cellsDay3.TeamId.Should().Be(TeamB);
}
```

- [ ] **Step 2: Run — expect pass (logic already handles this)**

Run: same as before.
Expected: 3 pass.

> If it fails, debug the `BucketByUserDayTeam` accumulation — both shifts on the same day should be summed per team.

- [ ] **Step 3: Commit (only if new test passed without code change — else commit fix first)**

```bash
git add tests/Humans.Application.Tests/Services/Shifts/VolunteerTrackingExportServiceTests.cs
git commit -m "test(shifts): multi-team day picks max-hours team"
```

---

### Task 3.6: Arrival day falls outside range — no white cell

- [ ] **Step 1: Append the failing test**

```csharp
[HumansFact]
public async Task ArrivalDayOutsideRange_NoWhiteCell_FirstInRangeCellColorsNormally()
{
    // Alice's first confirmed shift is exactly Day1 (=range start). Arrival = Day0 = outside.
    var shifts = new[]
    {
        ShiftRow(Alice, TeamA, "TeamA", Day1, 9, 17),
        ShiftRow(Alice, TeamA, "TeamA", Day1.PlusDays(1), 9, 17),
    };
    var (repo, shiftMgmt, users) = BuildMocks(
        shifts: shifts,
        departments: new[] { (TeamA, "TeamA") },
        playaNames: new Dictionary<Guid, string> { [Alice] = "Alice" });
    var sut = new VolunteerTrackingExportService(repo, shiftMgmt, users);

    var model = await sut.BuildAsync(BuildRequest(), ct: default);

    var cells = model.Groups[0].Humans[0].Cells;
    cells.Should().NotContain(c => c.Kind == CellKind.Arrival);
    cells[0].Kind.Should().Be(CellKind.Worked);
    cells[1].Kind.Should().Be(CellKind.Worked);
}
```

- [ ] **Step 2: Run — expect pass**

Already covered by current logic (arrival day computed as `firstShift - 1`; iteration only assigns Arrival when `d == arrivalDay` AND the day is in `days`). If fails, fix.

- [ ] **Step 3: Commit**

```bash
git add tests/Humans.Application.Tests/Services/Shifts/VolunteerTrackingExportServiceTests.cs
git commit -m "test(shifts): arrival day outside range hides white cell"
```

---

### Task 3.7: Department filter — only that team's work

- [ ] **Step 1: Append the failing test**

```csharp
private static readonly Guid Bob = Guid.Parse("b0000000-0000-0000-0000-000000000002");

[HumansFact]
public async Task DepartmentFilter_OnlyShowsThatDeptsWork()
{
    // Bob worked TeamA on Day3, TeamB on Day4. Filtered to TeamA: row appears,
    // Day3 colored, Day4 empty, arrival = Day2 (day before TeamA's first shift).
    var shifts = new[]
    {
        ShiftRow(Bob, TeamA, "TeamA", Day1.PlusDays(2), 9, 17),
        ShiftRow(Bob, TeamB, "TeamB", Day1.PlusDays(3), 9, 17),
    };
    // Repo respects the filter — return only TeamA's row (the repo test covers the SQL side).
    var teamAOnly = new[] { shifts[0] };
    var repo = Substitute.For<IVolunteerTrackingRepository>();
    repo.GetConfirmedShiftsInRangeAsync(EventId, Day1, Day7, TeamA, Arg.Any<CancellationToken>())
        .Returns(teamAOnly);
    var shiftMgmt = Substitute.For<IShiftManagementService>();
    shiftMgmt.GetDepartmentsWithRotasAsync(EventId).Returns(new[] { (TeamA, "TeamA") });
    var users = Substitute.For<IUserService>();
    users.GetUserInfoAsync(Bob, Arg.Any<CancellationToken>())
        .Returns(new UserInfo(Bob, "bob@test", "Bob", null, Array.Empty<string>()));

    var sut = new VolunteerTrackingExportService(repo, shiftMgmt, users);
    var model = await sut.BuildAsync(BuildRequest(departmentId: TeamA), ct: default);

    model.Groups.Should().HaveCount(1);
    var cells = model.Groups[0].Humans[0].Cells;
    cells[1].Kind.Should().Be(CellKind.Arrival);          // Day2
    cells[2].Kind.Should().Be(CellKind.Worked);           // Day3 — TeamA
    cells[3].Kind.Should().Be(CellKind.Empty);            // Day4 — TeamB excluded
}
```

- [ ] **Step 2: Run — expect pass**

Logic already correct: repo returns only filtered shifts; arrival computed from those filtered shifts only.

- [ ] **Step 3: Commit**

```bash
git add tests/Humans.Application.Tests/Services/Shifts/VolunteerTrackingExportServiceTests.cs
git commit -m "test(shifts): department filter scopes cells + arrival to that team"
```

---

### Task 3.8: Multi-day shift — hours clipped to each day

- [ ] **Step 1: Append the failing test**

```csharp
[HumansFact]
public async Task ShiftSpanningTwoDays_AppearsOnBothDays()
{
    // Alice has a TeamA shift starting 22:00 Day3 (local), ending 06:00 Day4 (local).
    // 2h on Day3, 6h on Day4.
    var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
    var startInstant = (Day1.PlusDays(2) + LocalTime.FromHourMinuteSecondTick(22, 0, 0, 0))
        .InZoneStrictly(zone).ToInstant();
    var endInstant = (Day1.PlusDays(3) + LocalTime.FromHourMinuteSecondTick(6, 0, 0, 0))
        .InZoneStrictly(zone).ToInstant();
    var shifts = new[] { new ConfirmedShiftRow(Alice, TeamA, "TeamA", startInstant, endInstant) };
    var (repo, shiftMgmt, users) = BuildMocks(
        shifts: shifts,
        departments: new[] { (TeamA, "TeamA") },
        playaNames: new Dictionary<Guid, string> { [Alice] = "Alice" });
    var sut = new VolunteerTrackingExportService(repo, shiftMgmt, users);

    var model = await sut.BuildAsync(BuildRequest(), ct: default);

    var cells = model.Groups[0].Humans[0].Cells;
    cells[2].Kind.Should().Be(CellKind.Worked); // Day3
    cells[3].Kind.Should().Be(CellKind.Worked); // Day4
}
```

- [ ] **Step 2: Run — expect pass**

The day-loop in `BucketByUserDayTeam` walks all local days that overlap the shift.

- [ ] **Step 3: Commit**

```bash
git add tests/Humans.Application.Tests/Services/Shifts/VolunteerTrackingExportServiceTests.cs
git commit -m "test(shifts): multi-day shifts contribute hours to both days"
```

---

### Task 3.9: Department group ordering + multi-team primary

- [ ] **Step 1: Append the failing test**

```csharp
private static readonly Guid Carol = Guid.Parse("c0000000-0000-0000-0000-000000000003");

[HumansFact]
public async Task GroupOrdering_ByTotalTeamHoursDescending_TieBreakOnName()
{
    // Alice: TeamA 8h Day3.
    // Bob: TeamA 8h Day3, TeamA 8h Day4 (TeamA total = 24h).
    // Carol: TeamB 8h Day3, TeamB 8h Day4, TeamB 8h Day5 (TeamB total = 24h, ties with TeamA).
    // Tie → alphabetical → TeamA first.
    var shifts = new[]
    {
        ShiftRow(Alice, TeamA, "TeamA", Day1.PlusDays(2), 9, 17),
        ShiftRow(Bob,   TeamA, "TeamA", Day1.PlusDays(2), 9, 17),
        ShiftRow(Bob,   TeamA, "TeamA", Day1.PlusDays(3), 9, 17),
        ShiftRow(Carol, TeamB, "TeamB", Day1.PlusDays(2), 9, 17),
        ShiftRow(Carol, TeamB, "TeamB", Day1.PlusDays(3), 9, 17),
        ShiftRow(Carol, TeamB, "TeamB", Day1.PlusDays(4), 9, 17),
    };
    var (repo, shiftMgmt, users) = BuildMocks(
        shifts: shifts,
        departments: new[] { (TeamA, "TeamA"), (TeamB, "TeamB") },
        playaNames: new Dictionary<Guid, string>
        {
            [Alice] = "Alice", [Bob] = "Bob", [Carol] = "Carol"
        });
    var sut = new VolunteerTrackingExportService(repo, shiftMgmt, users);

    var model = await sut.BuildAsync(BuildRequest(), ct: default);

    model.Groups.Should().HaveCount(2);
    model.Groups[0].TeamName.Should().Be("TeamA");  // tie → alpha
    model.Groups[1].TeamName.Should().Be("TeamB");
    model.Groups[0].Humans.Select(h => h.PlayaName).Should().Equal("Alice", "Bob");
    model.Groups[1].Humans.Select(h => h.PlayaName).Should().Equal("Carol");
}
```

- [ ] **Step 2: Fix the team-name placeholder + add total-team-hours ordering**

The Task 3.4 group-construction has a `"TODO-team-name"` placeholder and no group ordering. `g.Key` from `GroupBy(r => primaryTeam[r.Key])` is the `(teamId, teamName)` tuple, so destructure it directly. Add a total-hours-per-team dictionary and use it for the outer sort. Refactor to:

```csharp
// Total hours per team in scope.
var totalTeamHours = perUserPerDay
    .SelectMany(kvp => kvp.Value)
    .GroupBy(v => (v.teamId, v.teamName))
    .ToDictionary(g => g.Key.teamId, g => (g.Key.teamName, hours: g.Sum(v => v.hours)));

var groups = rows
    .GroupBy(r => primaryTeam[r.Key])
    .Select(g =>
    {
        var (teamId, teamName) = g.Key;
        var humans = g.Select(r => r.Value)
            .OrderBy(h => h.PlayaName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new DepartmentGroup(teamId, teamName, TeamPalette.ColorFor(teamId), humans);
    })
    .OrderByDescending(dg => totalTeamHours[dg.TeamId].hours)
    .ThenBy(dg => dg.TeamName, StringComparer.OrdinalIgnoreCase)
    .ToList();
```

- [ ] **Step 3: Run all tests — all pass**

Run: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --filter "FullyQualifiedName~VolunteerTrackingExportServiceTests" -v quiet`
Expected: 7 tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Services/Shifts/VolunteerTrackingExportService.cs \
        tests/Humans.Application.Tests/Services/Shifts/VolunteerTrackingExportServiceTests.cs
git commit -m "feat(shifts): group ordering by total team hours, tie-break alphabetical"
```

---

### Task 3.10: Service does not re-filter status (trusts the repo)

The repo's WHERE clause is the single source of truth for "only confirmed". A regression test documents that the service does not try to second-guess the filter.

- [ ] **Step 1: Append the failing test**

```csharp
[HumansFact]
public async Task ServiceTrustsRepoFilter_DoesNotReFilterByStatus()
{
    // Whatever the repo returns is treated as authoritative.
    // The repo's integration test (Chunk 2) covers the actual WHERE clause.
    var shifts = new[] { ShiftRow(Alice, TeamA, "TeamA", Day1.PlusDays(2), 9, 17) };
    var (repo, shiftMgmt, users) = BuildMocks(
        shifts: shifts,
        departments: new[] { (TeamA, "TeamA") },
        playaNames: new Dictionary<Guid, string> { [Alice] = "Alice" });
    var sut = new VolunteerTrackingExportService(repo, shiftMgmt, users);

    var model = await sut.BuildAsync(BuildRequest(), ct: default);

    model.Groups.Should().HaveCount(1);
    // The service does not call any status filter on the rows it receives.
    await repo.Received(1).GetConfirmedShiftsInRangeAsync(
        Arg.Any<Guid>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Run — expect pass**

- [ ] **Step 3: Commit**

```bash
git add tests/Humans.Application.Tests/Services/Shifts/VolunteerTrackingExportServiceTests.cs
git commit -m "test(shifts): service trusts repo's confirmed-only filter"
```

---

### Task 3.11: DI registration

**Files:**
- Modify: `src/Humans.Web/Extensions/Sections/ShiftsSectionExtensions.cs`

- [ ] **Step 1: Register the service**

Open the file. Find the `AddShiftsSection` extension method. After the existing repository registrations (around line 48), add:

```csharp
services.AddScoped<IVolunteerTrackingExportService, VolunteerTrackingExportService>();
```

Add the using at the top if not present:

```csharp
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Services.Shifts;
```

- [ ] **Step 2: Build whole solution**

Run: `dotnet build Humans.slnx -v quiet`
Expected: builds cleanly.

- [ ] **Step 3: Run the full test suite for safety**

Run: `dotnet test Humans.slnx -v quiet`
Expected: all green. No regressions in `ShiftDashboardController` tests.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Extensions/Sections/ShiftsSectionExtensions.cs
git commit -m "feat(shifts): register IVolunteerTrackingExportService for DI"
```

---

**End of Chunk 3.** Service produces a fully-formed `VolunteerExportModel` from request + repo data, with all rules under test. No XLSX file yet, no controller action.


## Chunk 4: XLSX builder — ClosedXML writes the file

This chunk turns a `VolunteerExportModel` into the actual `.xlsx` bytes. Pure transformation; no I/O beyond returning the byte array. Tested by writing the file in-memory, reopening with ClosedXML, and asserting cells/fills.

The builder is the home for **banner rows**, **header rows**, **frozen panes**, the **metadata block** (methodology + generated-by + filter summary), and the **totals row** formatting. The service hands it a structured model; the builder lays it onto a sheet.

### Task 4.1: Builder scaffold + metadata block round-trip

**Files:**
- Create: `src/Humans.Web/Models/VolunteerTracking/VolunteerTrackingXlsxBuilder.cs`
- Create: `tests/Humans.Web.Tests/Models/VolunteerTracking/VolunteerTrackingXlsxBuilderTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using ClosedXML.Excel;
using FluentAssertions;
using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Tests.Common;
using Humans.Web.Models.VolunteerTracking;
using NodaTime;

namespace Humans.Web.Tests.Models.VolunteerTracking;

public sealed class VolunteerTrackingXlsxBuilderTests
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 5, 23, 12, 0);

    [HumansFact]
    public void EmptyModel_ProducesValidXlsxWithMetadataBlock()
    {
        var model = new VolunteerExportModel(
            MethodologyBlurb: "Methodology text.",
            FilterSummary: "Department: All · Range: 2026-07-07 → 2026-07-13 (custom)",
            GeneratedAtUtc: TestNow,
            GeneratedByName: "TestActor",
            Days: new[] { new LocalDate(2026, 7, 7) },
            Groups: Array.Empty<DepartmentGroup>(),
            TotalsPerDay: new[] { 0 },
            SuggestedFileName: "volunteer-tracking-2026-07-07-to-2026-07-07.xlsx");

        var sut = new VolunteerTrackingXlsxBuilder();
        var result = sut.Build(model);

        result.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        result.FileName.Should().Be(model.SuggestedFileName);
        result.Content.Should().NotBeEmpty();

        using var stream = new MemoryStream(result.Content);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();

        sheet.Name.Should().Be("Volunteers");
        sheet.Cell("A1").GetString().Should().Contain("Volunteer tracking export").And.Contain("TestActor");
        sheet.Cell("A2").GetString().Should().Be("Department: All · Range: 2026-07-07 → 2026-07-13 (custom)");
        sheet.Cell("A3").GetString().Should().Be("Methodology text.");
    }
}
```

- [ ] **Step 2: Run — expect compile fail (no builder yet)**

Run: `dotnet build Humans.slnx -v quiet`
Expected: fails on missing `VolunteerTrackingXlsxBuilder`.

- [ ] **Step 3: Create the builder**

```csharp
using ClosedXML.Excel;
using Humans.Application.DTOs.VolunteerTrackingExport;

namespace Humans.Web.Models.VolunteerTracking;

public sealed record VolunteerTrackingXlsxResult(byte[] Content, string ContentType, string FileName);

public sealed class VolunteerTrackingXlsxBuilder
{
    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public VolunteerTrackingXlsxResult Build(VolunteerExportModel model)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Volunteers");

        WriteMetadataBlock(sheet, model);
        // Day headers + body come in later tasks.

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return new VolunteerTrackingXlsxResult(stream.ToArray(), XlsxContentType, model.SuggestedFileName);
    }

    private static void WriteMetadataBlock(IXLWorksheet sheet, VolunteerExportModel model)
    {
        var generatedAt = model.GeneratedAtUtc.ToString("uuuu-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
        sheet.Cell("A1").Value = $"Volunteer tracking export — generated {generatedAt} by {model.GeneratedByName}";
        sheet.Cell("A2").Value = model.FilterSummary;
        sheet.Cell("A3").Value = model.MethodologyBlurb;
        sheet.Cell("A3").Style.Alignment.WrapText = true;
        sheet.Cell("A3").Style.Font.Italic = true;
    }
}
```

- [ ] **Step 4: Run — expect pass**

Run: `dotnet test tests/Humans.Web.Tests/Humans.Web.Tests.csproj --filter "FullyQualifiedName~VolunteerTrackingXlsxBuilderTests" -v quiet`
Expected: 1 pass.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Models/VolunteerTracking/VolunteerTrackingXlsxBuilder.cs \
        tests/Humans.Web.Tests/Models/VolunteerTracking/VolunteerTrackingXlsxBuilderTests.cs
git commit -m "feat(shifts): XLSX builder scaffold + metadata block"
```

---

### Task 4.2: Day headers + freeze panes

- [ ] **Step 1: Add the failing test**

```csharp
[HumansFact]
public void DayHeaders_RenderDayOfWeekAndDate_InRows5And6()
{
    var days = new[]
    {
        new LocalDate(2026, 7, 7),  // Tue
        new LocalDate(2026, 7, 8),  // Wed
        new LocalDate(2026, 7, 9),  // Thu
    };
    var model = NewEmptyModel(days);
    var sut = new VolunteerTrackingXlsxBuilder();
    using var workbook = new XLWorkbook(new MemoryStream(sut.Build(model).Content));
    var sheet = workbook.Worksheets.First();

    // Column A reserved; day columns start at B.
    sheet.Cell("B5").GetString().Should().Be("Tue");
    sheet.Cell("C5").GetString().Should().Be("Wed");
    sheet.Cell("D5").GetString().Should().Be("Thu");
    sheet.Cell("B6").GetString().Should().Be("07/07/2026");
    sheet.Cell("C6").GetString().Should().Be("08/07/2026");
    sheet.Cell("D6").GetString().Should().Be("09/07/2026");

    sheet.SheetView.SplitRow.Should().Be(6);
    sheet.SheetView.SplitColumn.Should().Be(1);
}

private static VolunteerExportModel NewEmptyModel(IReadOnlyList<LocalDate> days) => new(
    MethodologyBlurb: "M.",
    FilterSummary: "F.",
    GeneratedAtUtc: TestNow,
    GeneratedByName: "Tester",
    Days: days,
    Groups: Array.Empty<DepartmentGroup>(),
    TotalsPerDay: Enumerable.Repeat(0, days.Count).ToArray(),
    SuggestedFileName: "x.xlsx");
```

- [ ] **Step 2: Run — expect fail**

Run: the builder test filter.
Expected: the day-headers test fails (cells empty / SplitRow=0).

- [ ] **Step 3: Extend the builder**

In `VolunteerTrackingXlsxBuilder.cs`, after the existing `WriteMetadataBlock(...)` call inside `Build`, add:

```csharp
WriteDayHeaders(sheet, model);
sheet.SheetView.FreezeRows(6);
sheet.SheetView.FreezeColumns(1);
```

Add the helper:

```csharp
private static void WriteDayHeaders(IXLWorksheet sheet, VolunteerExportModel model)
{
    for (var i = 0; i < model.Days.Count; i++)
    {
        var col = i + 2;  // start at column B
        var d = model.Days[i];
        sheet.Cell(5, col).Value = d.DayOfWeek.ToString().Substring(0, 3); // Mon, Tue, ...
        sheet.Cell(6, col).Value = $"{d.Day:D2}/{d.Month:D2}/{d.Year:D4}";
        sheet.Cell(5, col).Style.Font.Bold = true;
        sheet.Cell(6, col).Style.Font.Bold = true;
    }
}
```

- [ ] **Step 4: Run — expect both tests pass**

Run: filter as before.
Expected: 2 pass.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Models/VolunteerTracking/VolunteerTrackingXlsxBuilder.cs \
        tests/Humans.Web.Tests/Models/VolunteerTracking/VolunteerTrackingXlsxBuilderTests.cs
git commit -m "feat(shifts): day headers + frozen panes for XLSX export"
```

---

### Task 4.3: Department banner rows + colored fills

- [ ] **Step 1: Add the failing test**

```csharp
[HumansFact]
public void DepartmentBanner_RenderedAsMergedColoredRow()
{
    var days = new[] { new LocalDate(2026, 7, 7), new LocalDate(2026, 7, 8) };
    var teamA = Guid.Parse("11111111-0000-0000-0000-000000000000");
    var group = new DepartmentGroup(
        TeamId: teamA,
        TeamName: "Cantina",
        TeamColorHex: "#1F77B4",
        Humans: new[]
        {
            new HumanRow(Guid.NewGuid(), "Alice", new[] { CellState.Empty, CellState.Empty }),
        });
    var model = NewEmptyModel(days) with { Groups = new[] { group }, TotalsPerDay = new[] { 0, 0 } };

    var sut = new VolunteerTrackingXlsxBuilder();
    using var workbook = new XLWorkbook(new MemoryStream(sut.Build(model).Content));
    var sheet = workbook.Worksheets.First();

    // Body starts at row 7. Banner is row 7. Humans below from row 8.
    var bannerCell = sheet.Cell("A7");
    bannerCell.GetString().Should().Be("Cantina (1 humans)");
    bannerCell.Style.Fill.BackgroundColor.Color.ToHex().Should().EndWith("1F77B4");
    bannerCell.Style.Font.Bold.Should().BeTrue();
    bannerCell.Style.Font.FontColor.Color.Name.Should().Be("White");

    var merged = sheet.MergedRanges.FirstOrDefault(r => r.RangeAddress.FirstAddress.RowNumber == 7);
    merged.Should().NotBeNull();
    merged!.RangeAddress.LastAddress.ColumnNumber.Should().Be(3); // A..C (1 label + 2 day cols)
}
```

- [ ] **Step 2: Run — expect fail**

- [ ] **Step 3: Extend the builder**

In `Build`, after `WriteDayHeaders`, add:

```csharp
var nextRow = WriteGroupsAndHumans(sheet, model, startRow: 7);
WriteTotalsRow(sheet, model, totalsRow: nextRow);
```

Add the helper (humans and totals come in 4.4 / 4.5 — for now just the banner):

```csharp
private static int WriteGroupsAndHumans(IXLWorksheet sheet, VolunteerExportModel model, int startRow)
{
    var dayCount = model.Days.Count;
    var lastCol = dayCount + 1;  // 1 label + day columns
    var row = startRow;
    foreach (var group in model.Groups)
    {
        // Banner row
        var banner = sheet.Cell(row, 1);
        banner.Value = $"{group.TeamName} ({group.Humans.Count} humans)";
        var range = sheet.Range(row, 1, row, lastCol);
        range.Merge();
        range.Style.Fill.BackgroundColor = XLColor.FromHtml(group.TeamColorHex);
        range.Style.Font.Bold = true;
        range.Style.Font.FontColor = XLColor.White;
        row++;

        // Human rows come in Task 4.4 — for now, just advance the row counter to leave space.
        foreach (var _ in group.Humans) row++;
    }
    return row;
}

private static void WriteTotalsRow(IXLWorksheet sheet, VolunteerExportModel model, int totalsRow)
{
    // Filled in Task 4.5.
}
```

> Helper note: `XLColor.FromHtml` accepts `#RRGGBB`. `range.Style.Fill.BackgroundColor.Color.ToHex()` returns `FF1F77B4` (alpha-prefixed), hence `.EndWith("1F77B4")` in the assertion.

- [ ] **Step 4: Run — expect pass**

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Models/VolunteerTracking/VolunteerTrackingXlsxBuilder.cs \
        tests/Humans.Web.Tests/Models/VolunteerTracking/VolunteerTrackingXlsxBuilderTests.cs
git commit -m "feat(shifts): department banner rows in XLSX export"
```

---

### Task 4.4: Human rows — names + cell colors + arrival white

- [ ] **Step 1: Add the failing test**

```csharp
[HumansFact]
public void HumanRow_RendersNameAndColoredCells()
{
    var days = new[] { new LocalDate(2026, 7, 7), new LocalDate(2026, 7, 8), new LocalDate(2026, 7, 9) };
    var teamA = Guid.Parse("11111111-0000-0000-0000-000000000000");
    var aliceCells = new[]
    {
        CellState.Arrival,
        CellState.Worked(teamA, "#1F77B4"),
        CellState.Empty,
    };
    var group = new DepartmentGroup(
        TeamId: teamA,
        TeamName: "Cantina",
        TeamColorHex: "#1F77B4",
        Humans: new[] { new HumanRow(Guid.NewGuid(), "Alice", aliceCells) });
    var model = NewEmptyModel(days) with { Groups = new[] { group }, TotalsPerDay = new[] { 0, 1, 0 } };

    var sut = new VolunteerTrackingXlsxBuilder();
    using var workbook = new XLWorkbook(new MemoryStream(sut.Build(model).Content));
    var sheet = workbook.Worksheets.First();

    // Row 7 banner, row 8 Alice.
    sheet.Cell("A8").GetString().Should().Be("Alice");
    // Day 1 (col B) = Arrival — white fill, name in cell.
    sheet.Cell("B8").GetString().Should().Be("Alice");
    sheet.Cell("B8").Style.Fill.BackgroundColor.Color.Name.Should().Be("White");
    // Day 2 (col C) = Worked — team color fill, name.
    sheet.Cell("C8").GetString().Should().Be("Alice");
    sheet.Cell("C8").Style.Fill.BackgroundColor.Color.ToHex().Should().EndWith("1F77B4");
    // Day 3 (col D) = Empty — no fill, no text.
    sheet.Cell("D8").GetString().Should().BeEmpty();
    sheet.Cell("D8").Style.Fill.BackgroundColor.Color.Name.Should().NotBe("White");
}
```

- [ ] **Step 2: Run — expect fail**

- [ ] **Step 3: Update `WriteGroupsAndHumans`**

Replace the body of the method:

```csharp
private static int WriteGroupsAndHumans(IXLWorksheet sheet, VolunteerExportModel model, int startRow)
{
    var dayCount = model.Days.Count;
    var lastCol = dayCount + 1;
    var row = startRow;
    foreach (var group in model.Groups)
    {
        // Banner row
        var bannerRange = sheet.Range(row, 1, row, lastCol);
        sheet.Cell(row, 1).Value = $"{group.TeamName} ({group.Humans.Count} humans)";
        bannerRange.Merge();
        bannerRange.Style.Fill.BackgroundColor = XLColor.FromHtml(group.TeamColorHex);
        bannerRange.Style.Font.Bold = true;
        bannerRange.Style.Font.FontColor = XLColor.White;
        row++;

        // Human rows
        foreach (var human in group.Humans)
        {
            sheet.Cell(row, 1).Value = human.PlayaName;
            for (var i = 0; i < human.Cells.Count; i++)
            {
                var cell = sheet.Cell(row, i + 2);
                var state = human.Cells[i];
                switch (state.Kind)
                {
                    case CellKind.Empty:
                        // no value, no fill
                        break;
                    case CellKind.Arrival:
                        cell.Value = human.PlayaName;
                        cell.Style.Fill.BackgroundColor = XLColor.White;
                        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                        break;
                    case CellKind.Worked:
                        cell.Value = human.PlayaName;
                        cell.Style.Fill.BackgroundColor = XLColor.FromHtml(state.TeamColorHex!);
                        cell.Style.Font.FontColor = XLColor.White;
                        cell.Style.Font.Bold = true;
                        break;
                }
            }
            row++;
        }
    }
    return row;
}
```

- [ ] **Step 4: Run — expect pass**

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Models/VolunteerTracking/VolunteerTrackingXlsxBuilder.cs \
        tests/Humans.Web.Tests/Models/VolunteerTracking/VolunteerTrackingXlsxBuilderTests.cs
git commit -m "feat(shifts): render human rows with colored worked / white arrival cells"
```

---

### Task 4.5: Totals row at the bottom

- [ ] **Step 1: Add the failing test**

```csharp
[HumansFact]
public void TotalsRow_RendersUnderLastGroup_WithLabelAndPerDayCounts()
{
    var days = new[] { new LocalDate(2026, 7, 7), new LocalDate(2026, 7, 8) };
    var teamA = Guid.Parse("11111111-0000-0000-0000-000000000000");
    var humans = new[]
    {
        new HumanRow(Guid.NewGuid(), "Alice", new[] { CellState.Worked(teamA, "#1F77B4"), CellState.Empty }),
        new HumanRow(Guid.NewGuid(), "Bob",   new[] { CellState.Worked(teamA, "#1F77B4"), CellState.Worked(teamA, "#1F77B4") }),
    };
    var group = new DepartmentGroup(teamA, "Cantina", "#1F77B4", humans);
    var model = NewEmptyModel(days) with { Groups = new[] { group }, TotalsPerDay = new[] { 2, 1 } };

    var sut = new VolunteerTrackingXlsxBuilder();
    using var workbook = new XLWorkbook(new MemoryStream(sut.Build(model).Content));
    var sheet = workbook.Worksheets.First();

    // Row layout: 1-3 metadata, 4 blank, 5-6 day headers, 7 banner, 8 Alice, 9 Bob, 10 totals.
    sheet.Cell("A10").GetString().Should().Be("Total humans on-site");
    sheet.Cell("A10").Style.Font.Bold.Should().BeTrue();
    sheet.Cell("B10").GetDouble().Should().Be(2);
    sheet.Cell("C10").GetDouble().Should().Be(1);
    sheet.Cell("B10").Style.Font.Bold.Should().BeTrue();
}
```

- [ ] **Step 2: Run — expect fail**

- [ ] **Step 3: Implement `WriteTotalsRow`**

Replace the empty `WriteTotalsRow` with:

```csharp
private static void WriteTotalsRow(IXLWorksheet sheet, VolunteerExportModel model, int totalsRow)
{
    sheet.Cell(totalsRow, 1).Value = "Total humans on-site";
    sheet.Cell(totalsRow, 1).Style.Font.Bold = true;
    for (var i = 0; i < model.TotalsPerDay.Count; i++)
    {
        var cell = sheet.Cell(totalsRow, i + 2);
        cell.Value = model.TotalsPerDay[i];
        cell.Style.Font.Bold = true;
    }
}
```

- [ ] **Step 4: Run — expect pass**

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Models/VolunteerTracking/VolunteerTrackingXlsxBuilder.cs \
        tests/Humans.Web.Tests/Models/VolunteerTracking/VolunteerTrackingXlsxBuilderTests.cs
git commit -m "feat(shifts): totals row at bottom of XLSX export"
```

---

### Task 4.6: Empty-roster smoke + column auto-fit

A small polish task: when there are no groups, the file should still be valid (metadata + day headers + "no humans" hint). Auto-fit columns so the file looks reasonable on first open.

- [ ] **Step 1: Add the failing test**

```csharp
[HumansFact]
public void EmptyRoster_RendersHelpfulHintRow_AndColumnsAutoFit()
{
    var days = new[] { new LocalDate(2026, 7, 7) };
    var model = NewEmptyModel(days);
    var sut = new VolunteerTrackingXlsxBuilder();
    using var workbook = new XLWorkbook(new MemoryStream(sut.Build(model).Content));
    var sheet = workbook.Worksheets.First();

    sheet.Cell("A7").GetString().Should().Be("No confirmed humans in this range.");
    sheet.Cell("B7").GetString().Should().BeEmpty();      // no stray totals row
    sheet.Cell("A8").GetString().Should().BeEmpty();      // no totals row at all when empty
    sheet.Column(1).Width.Should().BeGreaterThan(0);
}
```

- [ ] **Step 2: Run — expect fail**

- [ ] **Step 3: Restructure `Build` so empty short-circuits before group/totals writes**

The current `Build` calls `WriteGroupsAndHumans` then `WriteTotalsRow` unconditionally. When `Groups.Count == 0`, the totals row still writes "Total humans on-site" into A7 and a `0` into B7 before any hint runs. Fix by branching:

```csharp
// Replace the existing unconditional
//   var nextRow = WriteGroupsAndHumans(sheet, model, startRow: 7);
//   WriteTotalsRow(sheet, model, totalsRow: nextRow);
// with:
if (model.Groups.Count == 0)
{
    sheet.Cell(7, 1).Value = "No confirmed humans in this range.";
    sheet.Cell(7, 1).Style.Font.Italic = true;
}
else
{
    var nextRow = WriteGroupsAndHumans(sheet, model, startRow: 7);
    WriteTotalsRow(sheet, model, totalsRow: nextRow);
}

sheet.Columns().AdjustToContents();
```

> If `AdjustToContents` is too slow for large grids (it can be — it measures text), wrap with `if (model.Groups.Count < 200)`. At ~500 humans this is fine.

- [ ] **Step 4: Run — all builder tests pass**

Run: `dotnet test tests/Humans.Web.Tests/Humans.Web.Tests.csproj --filter "FullyQualifiedName~VolunteerTrackingXlsxBuilderTests" -v quiet`
Expected: 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Models/VolunteerTracking/VolunteerTrackingXlsxBuilder.cs \
        tests/Humans.Web.Tests/Models/VolunteerTracking/VolunteerTrackingXlsxBuilderTests.cs
git commit -m "feat(shifts): empty-roster hint + auto-fit columns for XLSX"
```

---

**End of Chunk 4.** Calling `new VolunteerTrackingXlsxBuilder().Build(model)` produces a fully-formed `.xlsx` for any `VolunteerExportModel`. Builder is registered for DI in Chunk 5.

---

## Chunk 5: Controller + view + DI + i18n

This chunk wires the service + builder into a working HTTP endpoint and adds the Export card to the page. End of this chunk: the feature is shippable.

### Task 5.1: i18n resource keys for the Export card

The view needs a few new strings. Adding them first lets the view code reference real keys instead of inline strings.

**Files:**
- Modify: `src/Humans.Web/Resources/SharedResource.en.resx`
- Modify: `src/Humans.Web/Resources/SharedResource.es.resx`

- [ ] **Step 1: Identify the resource file paths**

Open `src/Humans.Web/Resources/`. Identify the English + Spanish shared resource files (file names may differ — confirm by reading existing keys like `VolTrack_Title`).

- [ ] **Step 2: Add English keys**

Add the following key/value pairs in alphabetical position:

| Key | English value |
|---|---|
| `VolTrack_Export_Title` | `Export volunteer grid` |
| `VolTrack_Export_Department` | `Department` |
| `VolTrack_Export_AllDepartments` | `All departments` |
| `VolTrack_Export_Period` | `Period` |
| `VolTrack_Export_PeriodCustom` | `Custom range` |
| `VolTrack_Export_From` | `From` |
| `VolTrack_Export_To` | `To` |
| `VolTrack_Export_Download` | `Download XLSX` |
| `VolTrack_Export_Methodology` | `Rows = humans with ≥1 confirmed shift in range. Cell color = the team they worked most hours that day. White cell = day before their first confirmed shift (arrival day). Totals row = humans on-site that day (used for meal counts). Names shown are playa names.` |

- [ ] **Step 3: Add Spanish keys**

Mirror with Spanish values. Use existing translation conventions in the file. If a phrase isn't obvious, leave the English value as a placeholder and add a translator comment (`<comment>TODO ES</comment>`).

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Resources/SharedResource.*.resx
git commit -m "i18n(shifts): add export-card resource keys (en + es)"
```

---

### Task 5.2: Export card form view-model

**Files:**
- Create: `src/Humans.Web/Models/VolunteerTracking/VolunteerTrackingExportFormViewModel.cs`

- [ ] **Step 1: Create the view-model**

```csharp
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models.VolunteerTracking;

public sealed class VolunteerTrackingExportFormViewModel
{
    public IReadOnlyList<(Guid TeamId, string TeamName)> Departments { get; init; } = Array.Empty<(Guid, string)>();
    public Guid? SelectedDepartmentId { get; init; }
    public ShiftPeriod? SelectedPeriod { get; init; }
    public LocalDate? StartDate { get; init; }
    public LocalDate? EndDate { get; init; }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: clean.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Models/VolunteerTracking/VolunteerTrackingExportFormViewModel.cs
git commit -m "feat(shifts): view-model for export form"
```

---

### Task 5.3: Add `ExportFormViewModel` to the page model

The existing `VolunteerTrackingPageViewModel` needs to carry the form state so the view can render the card.

**Files:**
- Modify: `src/Humans.Web/Models/VolunteerTrackingPageViewModel.cs`
- Modify: `src/Humans.Web/Controllers/VolunteerTrackingController.cs`

- [ ] **Step 1: Add the property to the page view-model**

Open `src/Humans.Web/Models/VolunteerTrackingPageViewModel.cs`. Add:

```csharp
public required VolunteerTrackingExportFormViewModel ExportForm { get; init; }
```

Add `using Humans.Web.Models.VolunteerTracking;` if not present.

- [ ] **Step 2: Populate it in `Index`**

In `VolunteerTrackingController.Index`, add a fetch for departments and populate `ExportForm`:

```csharp
var departments = await shiftManagementService.GetDepartmentsWithRotasAsync(eventSettings.Id);
// (existing code building viewModel)
viewModel.ExportForm = new VolunteerTrackingExportFormViewModel
{
    Departments = departments,
    SelectedDepartmentId = null,
    SelectedPeriod = ShiftPeriod.Event,
    StartDate = null,
    EndDate = null,
};
```

If `IShiftManagementService` isn't already injected into the controller, add it to the primary constructor.

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: clean.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Models/VolunteerTrackingPageViewModel.cs \
        src/Humans.Web/Controllers/VolunteerTrackingController.cs
git commit -m "feat(shifts): expose export-form state on volunteer tracking page"
```

---

### Task 5.4: Export card partial view

**Files:**
- Create: `src/Humans.Web/Views/VolunteerTracking/_ExportCard.cshtml`

- [ ] **Step 1: Create the partial**

```cshtml
@model Humans.Web.Models.VolunteerTracking.VolunteerTrackingExportFormViewModel
@using Humans.Domain.Enums

<details class="card mb-4" open>
    <summary class="card-header h5 mb-0" style="cursor: pointer;">
        @Localizer["VolTrack_Export_Title"]
    </summary>
    <div class="card-body">
        <form method="get" action="@Url.Action("ExportXlsx", "VolunteerTracking")" class="row g-3 align-items-end">
            <div class="col-md-3">
                <label class="form-label" for="exp-dept">@Localizer["VolTrack_Export_Department"]</label>
                <select id="exp-dept" name="departmentId" class="form-select">
                    <option value="">@Localizer["VolTrack_Export_AllDepartments"]</option>
                    @foreach (var d in Model.Departments)
                    {
                        <option value="@d.TeamId" selected="@(Model.SelectedDepartmentId == d.TeamId)">@d.TeamName</option>
                    }
                </select>
            </div>
            <div class="col-md-2">
                <label class="form-label" for="exp-period">@Localizer["VolTrack_Export_Period"]</label>
                <select id="exp-period" name="period" class="form-select">
                    <option value="">@Localizer["VolTrack_Export_PeriodCustom"]</option>
                    @foreach (var p in Enum.GetValues<ShiftPeriod>())
                    {
                        <option value="@p" selected="@(Model.SelectedPeriod == p)">@p</option>
                    }
                </select>
            </div>
            <div class="col-md-2">
                <label class="form-label" for="exp-from">@Localizer["VolTrack_Export_From"]</label>
                <input id="exp-from" name="startDate" type="date" class="form-control"
                       value="@(Model.StartDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))" />
            </div>
            <div class="col-md-2">
                <label class="form-label" for="exp-to">@Localizer["VolTrack_Export_To"]</label>
                <input id="exp-to" name="endDate" type="date" class="form-control"
                       value="@(Model.EndDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))" />
            </div>
            <div class="col-md-3">
                <button type="submit" class="btn btn-primary w-100">@Localizer["VolTrack_Export_Download"]</button>
            </div>
            <div class="col-12">
                <small class="text-muted fst-italic">@Localizer["VolTrack_Export_Methodology"]</small>
            </div>
        </form>
    </div>
</details>
```

- [ ] **Step 2: Render it from `Index.cshtml`**

Open `src/Humans.Web/Views/VolunteerTracking/Index.cshtml`. Find the existing first card (around line 44 — `<div class="card mb-4">`). Insert above it:

```cshtml
<partial name="_ExportCard" model="Model.ExportForm" />
```

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: clean.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/VolunteerTracking/_ExportCard.cshtml \
        src/Humans.Web/Views/VolunteerTracking/Index.cshtml
git commit -m "feat(shifts): export card UI on volunteer tracking page"
```

---

### Task 5.5: `ExportXlsx` controller action

**Files:**
- Modify: `src/Humans.Web/Controllers/VolunteerTrackingController.cs`
- Create: `tests/Humans.Web.Tests/Controllers/VolunteerTrackingControllerExportXlsxTests.cs`

- [ ] **Step 1: Write the failing controller test**

```csharp
using FluentAssertions;
using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Humans.Tests.Common;
using Humans.Web.Controllers;
using Humans.Web.Models.VolunteerTracking;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Controllers;

public sealed class VolunteerTrackingControllerExportXlsxTests
{
    [HumansFact]
    public async Task ExportXlsx_HappyPath_ReturnsFileContentResult()
    {
        var exportService = Substitute.For<IVolunteerTrackingExportService>();
        var model = new VolunteerExportModel(
            MethodologyBlurb: "M", FilterSummary: "F",
            GeneratedAtUtc: Instant.FromUtc(2026, 5, 23, 0, 0),
            GeneratedByName: "Actor",
            Days: new[] { new LocalDate(2026, 7, 7) },
            Groups: Array.Empty<DepartmentGroup>(),
            TotalsPerDay: new[] { 0 },
            SuggestedFileName: "volunteer-tracking-2026-07-07-to-2026-07-07.xlsx");
        exportService.BuildAsync(Arg.Any<VolunteerExportRequest>(), Arg.Any<CancellationToken>()).Returns(model);

        // Wire up the controller however the project's pattern does — see existing
        // VolunteerTrackingControllerTests (if present) for fixture / DI pattern.
        var sut = BuildController(exportService);

        var result = await sut.ExportXlsx(departmentId: null, startDate: null, endDate: null, period: ShiftPeriod.Event, ct: default);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        file.FileDownloadName.Should().Be(model.SuggestedFileName);
        file.FileContents.Should().NotBeEmpty();
    }

    private static VolunteerTrackingController BuildController(IVolunteerTrackingExportService exportService)
    {
        // Construct VolunteerTrackingController with whatever other dependencies it now requires.
        // Use NSubstitute mocks for all of them — the only behavior under test is the ExportXlsx wiring.
        // See VolunteerTrackingController's primary constructor for the full list.
        throw new NotImplementedException("Wire up controller constructor mocks.");
    }
}
```

> **Implementer note:** `BuildController` is intentionally a `throw` — wire the mocks after looking at the controller's primary constructor (which Task 5.3 may have widened to include `IShiftManagementService`).

- [ ] **Step 2: Run — expect compile-then-NotImplemented fail**

Run: `dotnet test tests/Humans.Web.Tests/Humans.Web.Tests.csproj --filter "FullyQualifiedName~VolunteerTrackingControllerExportXlsxTests" -v quiet`
Expected: compile fails because `ExportXlsx` doesn't exist yet.

- [ ] **Step 3: Add the controller action**

In `VolunteerTrackingController.cs`, inject `IVolunteerTrackingExportService` and `VolunteerTrackingXlsxBuilder` into the primary constructor. Add the action:

```csharp
[HttpGet("ExportXlsx")]
public async Task<IActionResult> ExportXlsx(
    Guid? departmentId,
    string? startDate,
    string? endDate,
    ShiftPeriod? period,
    CancellationToken ct = default)
{
    var eventSettings = await eventSettingsService.GetCurrentAsync(ct);  // adjust to project's actual call
    var pattern = LocalDatePattern.Iso;

    LocalDate? parsedStart = TryParse(startDate, pattern);
    LocalDate? parsedEnd = TryParse(endDate, pattern);
    var (activeStart, activeEnd) = ShiftFilterResolver.Resolve(period, parsedStart, parsedEnd);

    // Resolve range:
    //   period set → period's window (Build/Event/Strike each have distinct windows)
    //   period null + explicit dates → those dates
    //   period null + no dates → whole event (Build → Strike inclusive)
    LocalDate rangeStart, rangeEnd;
    if (period.HasValue)
    {
        (rangeStart, rangeEnd) = ShiftFilterResolver.ResolvePeriodRange(period.Value, eventSettings);
    }
    else if (activeStart.HasValue && activeEnd.HasValue)
    {
        (rangeStart, rangeEnd) = (activeStart.Value, activeEnd.Value);
    }
    else
    {
        // No period, no full range — fall back to the whole event window (Build through Strike).
        rangeStart = eventSettings.GateOpeningDate.PlusDays(eventSettings.BuildStartOffset);
        rangeEnd = eventSettings.GateOpeningDate.PlusDays(eventSettings.StrikeEndOffset);
    }
    if (rangeEnd < rangeStart) (rangeStart, rangeEnd) = (rangeEnd, rangeStart);

    var actor = (await userService.GetUserInfoAsync(User.GetUserId(), ct))?.BurnerName ?? "(unknown)";

    var request = new VolunteerExportRequest(
        EventSettingsId: eventSettings.Id,
        DepartmentId: departmentId,
        StartDate: rangeStart,
        EndDate: rangeEnd,
        Period: period,
        ActorPlayaName: actor,
        GeneratedAtUtc: SystemClock.Instance.GetCurrentInstant());

    var model = await exportService.BuildAsync(request, ct);
    var result = xlsxBuilder.Build(model);
    return File(result.Content, result.ContentType, result.FileName);

    static LocalDate? TryParse(string? input, LocalDatePattern pattern)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var parsed = pattern.Parse(input);
        return parsed.Success ? parsed.Value : null;
    }
}
```

Add usings:

```csharp
using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Application.Interfaces.Shifts;
using Humans.Web.Models.Shifts;
using Humans.Web.Models.VolunteerTracking;
using NodaTime;
using NodaTime.Text;
```

> **Implementer note:** the action references `eventSettingsService`, `userService`, `User.GetUserId()`, and event range field names. Adjust to whatever the project actually uses — look at `VolunteerTrackingController.Index` to see what's already injected and which `User` extension is used.

- [ ] **Step 4: Implement the test's `BuildController` helper**

Now that the controller's primary constructor is final, fill in `BuildController` with NSubstitute mocks for every constructor dependency.

- [ ] **Step 5: Run — expect pass**

Run: filter as before.
Expected: 1 pass.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Web/Controllers/VolunteerTrackingController.cs \
        tests/Humans.Web.Tests/Controllers/VolunteerTrackingControllerExportXlsxTests.cs
git commit -m "feat(shifts): ExportXlsx controller action"
```

---

### Task 5.6: Register `VolunteerTrackingXlsxBuilder` for DI

**Files:**
- Modify: `src/Humans.Web/Extensions/Sections/ShiftsSectionExtensions.cs`

- [ ] **Step 1: Add the registration**

Near the existing `services.AddScoped<IVolunteerTrackingExportService, VolunteerTrackingExportService>();` (from Chunk 3 Task 3.11), add:

```csharp
services.AddScoped<VolunteerTrackingXlsxBuilder>();
```

Add the using if not present:

```csharp
using Humans.Web.Models.VolunteerTracking;
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: clean.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Extensions/Sections/ShiftsSectionExtensions.cs
git commit -m "feat(shifts): register VolunteerTrackingXlsxBuilder for DI"
```

---

### Task 5.7: Full-solution test + manual smoke

- [ ] **Step 1: Run all tests**

Run: `dotnet test Humans.slnx -v quiet`
Expected: all green. No regressions.

- [ ] **Step 2: Run the app and exercise the feature**

Run: `dotnet run --project src/Humans.Web`

In a browser, log in (dev login if `DevAuth__Enabled=true`) as a user with `ShiftDashboardAccess`. Navigate to `/Shifts/Dashboard/VolunteerTracking`. Verify the Export card renders with:
- Department dropdown populated with real teams
- Period dropdown showing Build / Event / Strike
- "From" and "To" date inputs
- "Download XLSX" button
- Methodology paragraph beneath the form

Click "Download XLSX" with the default selections. Open the downloaded file in LibreOffice / Excel. Verify:
- Sheet named `Volunteers`
- Row 1: "Volunteer tracking export — generated YYYY-MM-DD HH:mm UTC by <your-playa-name>"
- Row 2: "Department: All · Range: ... (Event)"
- Row 3: methodology paragraph
- Rows 5–6: day headers (day-of-week, dd/MM/yyyy)
- Per-department banner rows with colored fills
- Human rows with colored cells per worked day, white arrival cells, names visible
- Bottom totals row labeled "Total humans on-site" with bold counts

Try again with a department selected — confirm only that team's humans appear, cells only fill for that team's days, filename includes the team slug.

- [ ] **Step 3: Capture & commit**

If anything looks off, fix in a follow-up commit before declaring done.

No commit if nothing changed.

---

**End of Chunk 5.** Feature is shippable: card on page, action wired, file downloads, builder produces colored grid, all rules tested. Ready for PR.

---

## Wrap-up: open PR

After all chunks pass, push the branch and open a PR to `origin/main` (Peter's fork). Use the project's PR template; reference the spec at `docs/superpowers/specs/2026-05-23-volunteer-tracking-export-design.md`. Preview env at `https://{pr_id}.n.burn.camp` will surface for manual verification.

### Follow-up TODOs (optional, post-merge)

- **DRY cleanup**: replace the two duplicate period→range switches in `src/Humans.Web/Models/Shifts/ShiftBrowsePageBuilder.cs:221` and `src/Humans.Web/Controllers/ShiftsController.cs:452` with calls to `ShiftFilterResolver.ResolvePeriodRange`. Pure refactor; covered by the existing tests of those callers.
