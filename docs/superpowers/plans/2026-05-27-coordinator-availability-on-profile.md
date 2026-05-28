# Coordinator Availability on the Profile — Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a Volunteer Coordinator/Admin see and edit a single volunteer's barrio-build window (declared days, camp-setup, day-offs) directly on that volunteer's profile, including a per-day "mark/unmark available" toggle.

**Architecture:** A new `VolunteerBuildStripViewComponent` (mirroring `ShiftSignupsViewComponent`) renders the existing `_VolunteerHeatmap.cshtml` partial scoped to one volunteer, fed by a new cross-section read method `IVolunteerTrackingServiceRead.GetUserBuildStripAsync`. All edits POST to `VolunteerTrackingController` (writes stay consolidated there) with a `returnUrl` so the coordinator lands back on the profile. Per-day availability mutation is a new method on `IGeneralAvailabilityService`.

**Tech Stack:** .NET / ASP.NET Core MVC, EF Core, NodaTime, Clean Architecture (Domain→Application→Infrastructure→Web). Tests: xUnit (`[HumansFact]`), AwesomeAssertions, NSubstitute, `ServiceTestHarness`.

**Source spec:** `docs/superpowers/specs/2026-05-27-coordinator-availability-on-profile-design.md`

**Build/test commands** (run from worktree root; `-v quiet` is mandatory):
- Build: `dotnet build Humans.slnx -v quiet`
- Full tests: `dotnet test Humans.slnx -v quiet`
- Scoped tests: `dotnet test tests/Humans.Application.Tests -v quiet --filter "FullyQualifiedName~<Class>"`

**Deviations from spec (deliberate, lower-risk):**
1. No shared `BuildCells` extraction — the two cohort loops differ; `GetUserBuildStripAsync` gets its own unified loop. The cohort paths are untouched (zero regression risk).
2. `VolunteerCell.DeclaredAvailable` is an **optional** positional param (`= false`) so existing `new VolunteerCell(...)` sites compile unchanged; it's populated only in the single-user path.
3. `SetDayAvailabilityAsync` guards `dayOffset >= 0` only (no EventSettings access in `GeneralAvailabilityService`); the lower build-window bound is unenforced and harmless (out-of-window offsets are filtered everywhere availability is read).

---

## Chunk 1: Domain + Application

### Task 1: Audit actions (Domain)

**Files:**
- Modify: `src/Humans.Domain/Enums/AuditAction.cs` (append at the very end — the enum is **positional**, stored by ordinal; never insert mid-enum)

- [ ] **Step 1: Append two values** after the last member (`CoordinatorRotaMessageSent`), before the closing `}`:

```csharp
    CoordinatorRotaMessageSent,
    // Coordinator edits a volunteer's declared build availability on their behalf
    // (Profile build strip). #<issue>.
    VolunteerAvailabilitySet,
    VolunteerAvailabilityCleared,
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Humans.Domain -v quiet`
Expected: success.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Domain/Enums/AuditAction.cs
git commit -m "feat(audit): add VolunteerAvailabilitySet/Cleared actions"
```

---

### Task 2: `VolunteerCell.DeclaredAvailable` + read DTO + read interface (Application)

**Files:**
- Modify: `src/Humans.Application/DTOs/VolunteerTrackingViewModel.cs`
- Create: `src/Humans.Application/Interfaces/Shifts/IVolunteerTrackingServiceRead.cs`
- Modify: `src/Humans.Application/Interfaces/Shifts/IVolunteerTrackingService.cs`

- [ ] **Step 1: Add the optional field to `VolunteerCell`**

In `VolunteerTrackingViewModel.cs`, change the record to add a trailing optional param:

```csharp
public sealed record VolunteerCell(
    int DayOffset,
    VolunteerCellState State,
    IReadOnlyList<string> RotaNames,
    bool DeclaredAvailable = false);
```

- [ ] **Step 2: Add the strip DTO** (same file, after `VolunteerTrackingViewModel`):

```csharp
/// <summary>One volunteer's build-window strip for the profile build-strip
/// view component. Reuses <see cref="VolunteerHeatmapRow"/>.</summary>
public sealed record VolunteerBuildStripDto(
    int BuildStartOffset,
    LocalDate GateOpeningDate,
    VolunteerHeatmapRow Row);
```

- [ ] **Step 3: Create the read interface** `IVolunteerTrackingServiceRead.cs` (mirror `ITeamServiceRead`):

```csharp
using Humans.Application.Architecture;
using Humans.Application.DTOs;

namespace Humans.Application.Interfaces.Shifts;

/// <summary>
/// Cross-section read surface for Volunteer Tracking. External sections inject
/// this (not the full <see cref="IVolunteerTrackingService"/>); returns only
/// the <see cref="VolunteerBuildStripDto"/> projection. See
/// <c>memory/architecture/section-read-write-split.md</c>.
/// </summary>
[SurfaceBudget(1)]
public interface IVolunteerTrackingServiceRead
{
    /// <summary>One volunteer's build-window strip, or null when there is no
    /// active event.</summary>
    Task<VolunteerBuildStripDto?> GetUserBuildStripAsync(Guid userId, CancellationToken ct = default);
}
```

(Confirm the `SurfaceBudget` attribute namespace matches `ITeamServiceRead`'s `using` — `Humans.Application.Architecture`.)

- [ ] **Step 4: Make the full interface inherit it**

In `IVolunteerTrackingService.cs` the current declaration is `public interface IVolunteerTrackingService : IApplicationService`. **Keep `IApplicationService`** and add the read interface:

```csharp
public interface IVolunteerTrackingService : IApplicationService, IVolunteerTrackingServiceRead
```

Dropping `IApplicationService` would break `ServiceBoundaryArchitectureTests.Application_boundary_interfaces_are_marked_as_application_services` (every `I*Service` must be `IApplicationService`-assignable; `IVolunteerTrackingServiceRead` ends in `Read` and is intentionally NOT marked, mirroring `ITeamServiceRead`). Do **not** add `GetUserBuildStripAsync` to the full interface — it's inherited from the read interface.

- [ ] **Step 5: Build (will fail — `VolunteerTrackingService` doesn't implement the new method yet)**

Run: `dotnet build src/Humans.Application -v quiet`
Expected: FAIL — `VolunteerTrackingService` does not implement `GetUserBuildStripAsync`. (Implemented in Task 3.)

---

### Task 3: `GetUserBuildStripAsync` implementation + tests (Application)

**Files:**
- Modify: `src/Humans.Application/Services/Shifts/VolunteerTrackingService.cs`
- Test: `tests/Humans.Application.Tests/Services/Shifts/VolunteerTrackingServiceTests.cs`

- [ ] **Step 1: Write failing tests** — add to `VolunteerTrackingServiceTests` (follow the existing `BuildSut`/`MakeEvent`/`EligibleBuildSignup`/`Participation` helpers in that file; read them first):

```csharp
[HumansFact]
public async Task GetUserBuildStrip_returns_null_when_no_active_event()
{
    var sut = BuildSut(activeEvent: null);
    (await sut.GetUserBuildStripAsync(Guid.NewGuid())).Should().BeNull();
}

[HumansFact]
public async Task GetUserBuildStrip_marks_declared_days_available()
{
    var es = MakeEvent(buildStartOffset: -3);          // offsets -3,-2,-1
    var userId = Guid.NewGuid();
    // Harness default: GateOpening 2026-06-16, clock 2026-06-15 → todayOffset = -1.
    var sut = BuildSut(es,
        participations: new[] { Participation(userId, ParticipationStatus.Ticketed, es.Year) },
        availabilities: new[] { Availability(userId, es.Id, [-2, -1]) });

    var strip = await sut.GetUserBuildStripAsync(userId);

    strip.Should().NotBeNull();
    // Declared flag set on exactly the declared days:
    strip!.Row.Cells.Where(c => c.DeclaredAvailable).Select(c => c.DayOffset)
        .Should().BeEquivalentTo(new[] { -2, -1 });
    // Precedence: -2 is before today → AvailableUnbooked; -1 is today/future → AvailableExpected.
    strip.Row.Cells.Single(c => c.DayOffset == -2).State.Should().Be(VolunteerCellState.AvailableUnbooked);
    strip.Row.Cells.Single(c => c.DayOffset == -1).State.Should().Be(VolunteerCellState.AvailableExpected);
}

[HumansFact]
public async Task GetUserBuildStrip_reflects_signups_and_dayoffs()
{
    var es = MakeEvent(buildStartOffset: -3);
    var userId = Guid.NewGuid();
    var sut = BuildSut(es,
        signups: new[] { new EligibleBuildSignup(userId, -3, SignupStatus.Confirmed, "Cleanup") },
        participations: new[] { Participation(userId, ParticipationStatus.Ticketed, es.Year) });

    var strip = await sut.GetUserBuildStripAsync(userId);

    strip!.Row.Cells.Single(c => c.DayOffset == -3).State.Should().Be(VolunteerCellState.Confirmed);
}

[HumansFact]
public async Task GetUserBuildStrip_returns_row_for_user_with_no_availability_row()
{
    var es = MakeEvent(buildStartOffset: -2);
    var userId = Guid.NewGuid();
    var sut = BuildSut(es, participations: new[] { Participation(userId, ParticipationStatus.Ticketed, es.Year) });

    var strip = await sut.GetUserBuildStripAsync(userId);

    strip.Should().NotBeNull();
    strip!.Row.Cells.Should().OnlyContain(c => c.DeclaredAvailable == false);
}
```

> **Harness extension required.** `BuildSut` already has an `availabilities:` parameter and a private `Availability(userId, esId, IReadOnlyList<int> offsets)` factory (reuse both). BUT its fake `IGeneralAvailabilityRepository` currently stubs only `GetByEventAsync` — it does **not** stub `GetByUserAndEventAsync`, which `GetUserBuildStripAsync` calls. Add that stub to `BuildSut` (return the matching `Availability` from the `availabilities` arg, or null). Without it NSubstitute returns null and the declared-day tests fail.

- [ ] **Step 2: Run — verify FAIL** (no implementation / not compiling)

Run: `dotnet test tests/Humans.Application.Tests -v quiet --filter "FullyQualifiedName~VolunteerTrackingServiceTests.GetUserBuildStrip"`
Expected: FAIL.

- [ ] **Step 3: Implement `GetUserBuildStripAsync`** — append to `VolunteerTrackingService` (after `GetTrackingDataAsync`):

```csharp
public async Task<VolunteerBuildStripDto?> GetUserBuildStripAsync(
    Guid userId, CancellationToken ct = default)
{
    var es = await shiftManagement.GetActiveEventSettingsAsync(ct).ConfigureAwait(false);
    if (es is null) return null;

    var zone = DateTimeZoneProviders.Tzdb[es.TimeZoneId];
    var todayOffset = OffsetOf(es, clock.GetCurrentInstant().InZone(zone).Date);

    var signups = await trackingRepo.GetEligibleBuildSignupsAsync(es.Id, ct).ConfigureAwait(false);
    var daySignups = signups
        .Where(s => s.UserId == userId)
        .GroupBy(s => s.DayOffset)
        .ToDictionary(
            g => g.Key,
            g => (
                Status: g.Any(x => x.Status == SignupStatus.Confirmed)
                    ? SignupStatus.Confirmed : SignupStatus.Pending,
                RotaNames: (IReadOnlyList<string>)g.Select(x => x.RotaName)
                    .Distinct(StringComparer.Ordinal).ToList()));

    var bs = (await trackingRepo.GetByEventAsync(es.Id, ct).ConfigureAwait(false))
        .FirstOrDefault(r => r.UserId == userId);
    int? setupOffset = bs?.BarrioSetupStartDate is { } d ? OffsetOf(es, d) : null;
    var dayOffSet = bs?.DayOffs.Select(x => x.DayOffset).ToHashSet() ?? [];

    var availRow = await availability.GetByUserAndEventAsync(userId, es.Id, ct).ConfigureAwait(false);
    var availSet = availRow?.AvailableDayOffsets.ToHashSet() ?? [];

    var hasSignups = daySignups.Count > 0;
    int firstSignupDay = hasSignups ? daySignups.Keys.Min() : 0;
    var lastExpectedDay = Math.Min(setupOffset ?? int.MaxValue, 0);

    var cells = new List<VolunteerCell>(-es.BuildStartOffset);
    int gapCount = 0;
    for (int day = es.BuildStartOffset; day < 0; day++)
    {
        bool declared = availSet.Contains(day);
        VolunteerCellState state;
        IReadOnlyList<string> rotaNames = [];

        if (setupOffset.HasValue && day >= setupOffset.Value)
            state = VolunteerCellState.CampSetup;
        else if (daySignups.TryGetValue(day, out var info))
        {
            state = info.Status == SignupStatus.Confirmed
                ? VolunteerCellState.Confirmed : VolunteerCellState.Pending;
            rotaNames = info.RotaNames;
        }
        else if (dayOffSet.Contains(day))
            state = VolunteerCellState.DayOff;
        else if (hasSignups && day >= firstSignupDay && day < lastExpectedDay)
        {
            state = VolunteerCellState.Gap;
            gapCount++;
        }
        else if (declared && day < todayOffset)
            state = VolunteerCellState.AvailableUnbooked;
        else if (declared)
            state = VolunteerCellState.AvailableExpected;
        else
            state = VolunteerCellState.Outside;

        cells.Add(new VolunteerCell(day, state, rotaNames, declared));
    }

    var dayOffSummaries = (bs?.DayOffs ?? [])
        .Select(x => new DayOffSummary(x.DayOffset, x.Reason))
        .ToList();

    var row = new VolunteerHeatmapRow(
        userId,
        firstSignupDay,
        hasSignups ? daySignups.Keys.Max() : 0,
        bs?.BarrioSetupStartDate,
        gapCount,
        cells,
        dayOffSummaries);

    return new VolunteerBuildStripDto(es.BuildStartOffset, es.GateOpeningDate, row);
}
```

> Names: `VolunteerTrackingService` uses a **primary constructor** — reference params directly as `shiftManagement`, `trackingRepo`, `availability`, `clock` (NO underscores). `OffsetOf` is the existing `private static` helper. `availability` is `IGeneralAvailabilityRepository` and exposes `GetByUserAndEventAsync(userId, esId, ct)`.

- [ ] **Step 4: Run — verify PASS** (and the existing cohort tests still pass)

Run: `dotnet test tests/Humans.Application.Tests -v quiet --filter "FullyQualifiedName~VolunteerTrackingServiceTests"`
Expected: PASS (new + existing).

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/DTOs/VolunteerTrackingViewModel.cs \
        src/Humans.Application/Interfaces/Shifts/IVolunteerTrackingServiceRead.cs \
        src/Humans.Application/Interfaces/Shifts/IVolunteerTrackingService.cs \
        src/Humans.Application/Services/Shifts/VolunteerTrackingService.cs \
        tests/Humans.Application.Tests/Services/Shifts/VolunteerTrackingServiceTests.cs
git commit -m "feat(shifts): GetUserBuildStripAsync read for single-volunteer build strip"
```

---

### Task 4: `SetDayAvailabilityAsync` + tests (Application)

**Files:**
- Modify: `src/Humans.Application/Interfaces/Shifts/IGeneralAvailabilityService.cs`
- Modify: `src/Humans.Application/Services/Shifts/GeneralAvailabilityService.cs`
- Test: `tests/Humans.Application.Tests/Services/Shifts/GeneralAvailabilityServiceTests.cs`

- [ ] **Step 1: Write failing tests** (the harness uses a real `GeneralAvailabilityRepository` + `Db`/`SeedEventSettings()`):

```csharp
[HumansFact]
public async Task SetDayAvailability_AddsOffset_PreservingExisting()
{
    var userId = Guid.NewGuid();
    var esId = SeedEventSettings();
    await Db.SaveChangesAsync();
    await _service.SetAvailabilityAsync(userId, esId, [-3]);

    await _service.SetDayAvailabilityAsync(userId, esId, -2, available: true);

    var rec = await Db.GeneralAvailability.AsNoTracking()
        .FirstAsync(g => g.UserId == userId && g.EventSettingsId == esId);
    rec.AvailableDayOffsets.Should().BeEquivalentTo([-3, -2]);
}

[HumansFact]
public async Task SetDayAvailability_RemovesOffset()
{
    var userId = Guid.NewGuid();
    var esId = SeedEventSettings();
    await Db.SaveChangesAsync();
    await _service.SetAvailabilityAsync(userId, esId, [-3, -2]);

    await _service.SetDayAvailabilityAsync(userId, esId, -2, available: false);

    var rec = await Db.GeneralAvailability.AsNoTracking()
        .FirstAsync(g => g.UserId == userId && g.EventSettingsId == esId);
    rec.AvailableDayOffsets.Should().BeEquivalentTo([-3]);
}

[HumansFact]
public async Task SetDayAvailability_CreatesRowWhenNoneExists()
{
    var userId = Guid.NewGuid();
    var esId = SeedEventSettings();
    await Db.SaveChangesAsync();

    await _service.SetDayAvailabilityAsync(userId, esId, -1, available: true);

    var rec = await Db.GeneralAvailability.AsNoTracking()
        .FirstOrDefaultAsync(g => g.UserId == userId && g.EventSettingsId == esId);
    rec.Should().NotBeNull();
    rec!.AvailableDayOffsets.Should().BeEquivalentTo([-1]);
}

[HumansFact]
public async Task SetDayAvailability_RejectsPositiveOffset()
{
    var userId = Guid.NewGuid();
    var esId = SeedEventSettings();
    await Db.SaveChangesAsync();

    await _service.SetDayAvailabilityAsync(userId, esId, 2, available: true);

    var rec = await Db.GeneralAvailability.AsNoTracking()
        .FirstOrDefaultAsync(g => g.UserId == userId && g.EventSettingsId == esId);
    rec.Should().BeNull(); // no-op, no row created
}
```

- [ ] **Step 2: Run — verify FAIL**

Run: `dotnet test tests/Humans.Application.Tests -v quiet --filter "FullyQualifiedName~GeneralAvailabilityServiceTests.SetDayAvailability"`
Expected: FAIL (method missing).

- [ ] **Step 3: Add to interface** `IGeneralAvailabilityService.cs`:

```csharp
/// Add (available=true) or remove (available=false) one build-day offset from
/// the user's declared availability. Read-modify-write; preserves other
/// offsets; invalidates the user's shift view cache. No-op for positive
/// (event-day) offsets — build availability is negative offsets only.
Task SetDayAvailabilityAsync(
    Guid userId, Guid eventSettingsId, int dayOffset, bool available,
    CancellationToken ct = default);
```

- [ ] **Step 4: Implement** in `GeneralAvailabilityService.cs`:

```csharp
public async Task SetDayAvailabilityAsync(
    Guid userId, Guid eventSettingsId, int dayOffset, bool available,
    CancellationToken ct = default)
{
    var current = await repo.GetByUserAndEventAsync(userId, eventSettingsId, ct).ConfigureAwait(false);
    var offsets = current?.AvailableDayOffsets.ToList() ?? [];

    if (available)
    {
        if (dayOffset >= 0 || offsets.Contains(dayOffset)) return; // event-day or no-op
        offsets.Add(dayOffset);
    }
    else if (!offsets.Remove(dayOffset))
    {
        return; // nothing to remove
    }

    offsets.Sort();
    await repo.UpsertAsync(userId, eventSettingsId, offsets, clock.GetCurrentInstant(), ct).ConfigureAwait(false);
    viewInvalidator.InvalidateUser(userId);
}
```

- [ ] **Step 5: Run — verify PASS**

Run: `dotnet test tests/Humans.Application.Tests -v quiet --filter "FullyQualifiedName~GeneralAvailabilityServiceTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Interfaces/Shifts/IGeneralAvailabilityService.cs \
        src/Humans.Application/Services/Shifts/GeneralAvailabilityService.cs \
        tests/Humans.Application.Tests/Services/Shifts/GeneralAvailabilityServiceTests.cs
git commit -m "feat(shifts): SetDayAvailabilityAsync per-day availability mutation"
```

---

### Chunk 1 gate
- [ ] `dotnet build Humans.slnx -v quiet` succeeds.
- [ ] `dotnet test tests/Humans.Application.Tests -v quiet` passes.

---

## Chunk 2: Web (DI, controller, partial, view component, view, i18n)

### Task 5: DI registration of the read interface

**Files:**
- Modify: `src/Humans.Web/Extensions/Sections/ShiftsSectionExtensions.cs` (~line 59)

- [ ] **Step 1:** Replace the direct registration with the concrete-first pattern (mirrors `GeneralAvailabilityService`, lines 53–55):

```csharp
// was: services.AddScoped<IVolunteerTrackingService, ShiftsVolunteerTrackingService>();
services.AddScoped<ShiftsVolunteerTrackingService>();
services.AddScoped<IVolunteerTrackingService>(sp => sp.GetRequiredService<ShiftsVolunteerTrackingService>());
services.AddScoped<IVolunteerTrackingServiceRead>(sp => sp.GetRequiredService<ShiftsVolunteerTrackingService>());
```

- [ ] **Step 2: Build** — `dotnet build src/Humans.Web -v quiet`. Expected: success.
- [ ] **Step 3: Commit** — `git commit -am "chore(di): register IVolunteerTrackingServiceRead"`

---

### Task 6: Controller — new availability actions + `returnUrl` + tests

**Files:**
- Modify: `src/Humans.Web/Controllers/VolunteerTrackingController.cs`
- Test: `tests/Humans.Web.Tests/Controllers/VolunteerTrackingControllerTests.cs`

- [ ] **Step 1: Write failing controller tests** (follow the existing harness in that file — how it constructs the controller, fakes services, sets the user/policy). Cover:

```csharp
// SetAvailabilityDay with VolunteerTrackingWrite → calls service(available:true) + audits VolunteerAvailabilitySet + redirects.
// ClearAvailabilityDay → service(available:false) + audits VolunteerAvailabilityCleared.
// returnUrl local ("/Profile/<guid>") → LocalRedirectResult to that path.
// returnUrl external ("https://evil.test") → RedirectToActionResult to Index (Url.IsLocalUrl false).
// Existing SetDayOff with returnUrl=null → still RedirectToActionResult to Index (no regression).
```

Use NSubstitute to assert `availabilityService.Received().SetDayAvailabilityAsync(userId, esId, dayOffset, true, Arg.Any<CancellationToken>())` and `auditLogService.Received().LogAsync(AuditAction.VolunteerAvailabilitySet, ...)`. Stub `shiftManagementService.GetActiveAsync()` to return an `EventSettings` with a known `Id`.

- [ ] **Step 2: Run — verify FAIL.**
Run: `dotnet test tests/Humans.Web.Tests -v quiet --filter "FullyQualifiedName~VolunteerTrackingControllerTests"`
Expected: FAIL.

- [ ] **Step 3: Add ONE ctor dep.** The controller already has a 7-param primary constructor (incl. `IShiftManagementService shiftManagementService`, `exportService`, `xlsxBuilder`, `IUserServiceRead userService`). **Only add `IGeneralAvailabilityService availabilityService`** — do NOT rename `shiftManagementService`, drop the export deps, or change `userService` to a non-Read type (the base `HumansControllerBase` takes `IUserServiceRead`). Result:

```csharp
public sealed class VolunteerTrackingController(
    IVolunteerTrackingService service,
    IShiftManagementService shiftManagementService,
    IGeneralAvailabilityService availabilityService,   // <-- added
    IVolunteerTrackingExportService exportService,
    VolunteerTrackingXlsxBuilder xlsxBuilder,
    IUserServiceRead userService,
    IAuditLogService auditLogService,
    IStringLocalizer<SharedResource> localizer) : HumansControllerBase(userService)
```

`using Humans.Application.Interfaces.Shifts;` is already present.

- [ ] **Step 4: Add a redirect helper** (private, near the bottom of the controller):

```csharp
private IActionResult RedirectBack(string? returnUrl) =>
    Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl!) : RedirectToAction(nameof(Index));
```

- [ ] **Step 5: Add the two new actions** (after `ClearDayOff`):

```csharp
[HttpPost("SetAvailabilityDay")]
[Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SetAvailabilityDay(
    Guid userId, int dayOffset, string? returnUrl, CancellationToken ct)
{
    var es = await shiftManagementService.GetActiveAsync();
    if (es is null) { SetError(localizer["VolTrack_Err_BadRequest"]); return RedirectBack(returnUrl); }

    var current = await GetCurrentUserInfoAsync();
    if (current is null) return Forbid();

    await availabilityService.SetDayAvailabilityAsync(userId, es.Id, dayOffset, true, ct);
    await auditLogService.LogAsync(
        AuditAction.VolunteerAvailabilitySet, nameof(GeneralAvailability), userId,
        $"DayOffset={dayOffset}; marked available by coordinator", current.Id);
    SetSuccess(localizer["VolTrack_Msg_AvailabilitySet"]);
    return RedirectBack(returnUrl);
}

[HttpPost("ClearAvailabilityDay")]
[Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]
[ValidateAntiForgeryToken]
public async Task<IActionResult> ClearAvailabilityDay(
    Guid userId, int dayOffset, string? returnUrl, CancellationToken ct)
{
    var es = await shiftManagementService.GetActiveAsync();
    if (es is null) { SetError(localizer["VolTrack_Err_BadRequest"]); return RedirectBack(returnUrl); }

    var current = await GetCurrentUserInfoAsync();
    if (current is null) return Forbid();

    await availabilityService.SetDayAvailabilityAsync(userId, es.Id, dayOffset, false, ct);
    await auditLogService.LogAsync(
        AuditAction.VolunteerAvailabilityCleared, nameof(GeneralAvailability), userId,
        $"DayOffset={dayOffset}; availability cleared by coordinator", current.Id);
    SetSuccess(localizer["VolTrack_Msg_AvailabilityCleared"]);
    return RedirectBack(returnUrl);
}
```

> Confirm `GeneralAvailability` is in scope for `nameof(GeneralAvailability)` — add `using Humans.Domain.Entities;` (already present).

- [ ] **Step 6: Thread `returnUrl` through the 4 existing write actions.** For `SetCampSetup`, `ClearCampSetup`, `SetDayOff`, `ClearDayOff`: add a `string? returnUrl` parameter and replace every `return RedirectToAction(nameof(Index));` in those actions with `return RedirectBack(returnUrl);`. (For the form-bound actions, `returnUrl` binds from the new hidden field. For `ClearCampSetup(Guid userId, ...)` add `string? returnUrl` alongside.)

- [ ] **Step 7: Run — verify PASS.**
Run: `dotnet test tests/Humans.Web.Tests -v quiet --filter "FullyQualifiedName~VolunteerTrackingControllerTests"`
Expected: PASS (incl. existing export/other tests in the file).

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Web/Controllers/VolunteerTrackingController.cs \
        tests/Humans.Web.Tests/Controllers/VolunteerTrackingControllerTests.cs
git commit -m "feat(shifts): availability-day write actions + returnUrl on tracking writes"
```

---

### Task 7: Partial — `ShowAvailabilityControls`, controller-qualified forms, returnUrl, availability toggle

**Files:**
- Modify: `src/Humans.Web/Models/VolunteerHeatmapPartialModels.cs`
- Modify: `src/Humans.Web/Views/VolunteerTracking/_VolunteerHeatmap.cshtml`

- [ ] **Step 1: Add the flag to `HeatmapPartialModel`** (optional trailing param so the tracking page's construction compiles unchanged):

```csharp
public sealed record HeatmapPartialModel(
    IReadOnlyList<VolunteerHeatmapRow> Rows,
    int BuildStartOffset,
    LocalDate GateOpeningDate,
    IReadOnlyDictionary<Guid, string> DisplayNameByUserId,
    bool ShowAvailabilityControls = false);
```

- [ ] **Step 2: In `_VolunteerHeatmap.cshtml`, compute `returnUrl`** — inside the top `@{ }` block (after `canWrite`):

```csharp
var returnUrl = $"{Context.Request.Path}{Context.Request.QueryString}";
```

- [ ] **Step 3: Profile-context card title.** Replace the card-header title/subtitle so a single-user profile strip reads correctly. In the `<div class="card-header ...">`:

```razor
@if (Model.ShowAvailabilityControls)
{
    <strong><i class="fa-solid fa-table-cells me-2"></i>@Localizer["Profile_BuildStrip_Title"]</strong>
}
else
{
    <strong><i class="fa-solid fa-table-cells me-2"></i>@Localizer["VolTrack_Main_Title"]</strong>
    <span class="text-muted small ms-2">@string.Format(Localizer["VolTrack_Main_Subtitle"].Value, Model.Rows.Count)</span>
}
```

- [ ] **Step 4: Qualify existing forms + add returnUrl.** On each existing `<form method="post" asp-action="...">` in the popover (SetCampSetup, ClearCampSetup, SetDayOff, ClearDayOff), add `asp-controller="VolunteerTracking"` and a hidden field `<input type="hidden" name="returnUrl" value="@returnUrl" />`.

- [ ] **Step 5: Add the availability toggle** at the end of the `@if (canWrite)` block (after the day-off section), gated to non-booked, non-campsetup cells:

```razor
@if (Model.ShowAvailabilityControls
     && cell.State != VolunteerCellState.CampSetup
     && cell.State != VolunteerCellState.Confirmed
     && cell.State != VolunteerCellState.Pending)
{
    <hr class="my-2" />
    <div class="small text-muted mb-1">@Localizer["Profile_BuildStrip_Title"]</div>
    @if (cell.DeclaredAvailable)
    {
        <form method="post" asp-controller="VolunteerTracking" asp-action="ClearAvailabilityDay">
            @Html.AntiForgeryToken()
            <input type="hidden" name="userId" value="@row.UserId" />
            <input type="hidden" name="dayOffset" value="@cell.DayOffset" />
            <input type="hidden" name="returnUrl" value="@returnUrl" />
            <button type="submit" class="btn btn-sm btn-outline-danger w-100">
                <i class="fa-solid fa-minus me-1"></i>@Localizer["VolTrack_Popover_UnmarkAvailable"]
            </button>
        </form>
    }
    else
    {
        <form method="post" asp-controller="VolunteerTracking" asp-action="SetAvailabilityDay">
            @Html.AntiForgeryToken()
            <input type="hidden" name="userId" value="@row.UserId" />
            <input type="hidden" name="dayOffset" value="@cell.DayOffset" />
            <input type="hidden" name="returnUrl" value="@returnUrl" />
            <button type="submit" class="btn btn-sm btn-outline-success w-100">
                <i class="fa-solid fa-plus me-1"></i>@Localizer["VolTrack_Popover_MarkAvailable"]
            </button>
        </form>
    }
}
```

- [ ] **Step 6: Build** — `dotnet build src/Humans.Web -v quiet`. Expected: success (Razor compiles at build with the SDK's razor compilation; if not, it's exercised at runtime in Task 10).
- [ ] **Step 7: Commit** — `git commit -am "feat(shifts): availability toggle + returnUrl in heatmap partial"`

---

### Task 8: `VolunteerBuildStripViewComponent`

**Files:**
- Create: `src/Humans.Web/ViewComponents/VolunteerBuildStripViewComponent.cs`

- [ ] **Step 1: Create the component** (mirror `ShiftSignupsViewComponent`):

```csharp
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public class VolunteerBuildStripViewComponent(
    IVolunteerTrackingServiceRead tracking,
    IUserServiceRead userService,
    ILogger<VolunteerBuildStripViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        try
        {
            var strip = await tracking.GetUserBuildStripAsync(userId);
            if (strip is null) return Content(string.Empty);

            var info = await userService.GetUserInfoAsync(userId);
            var names = new Dictionary<Guid, string> { [userId] = info?.BurnerName ?? string.Empty };

            var model = new HeatmapPartialModel(
                [strip.Row],
                strip.BuildStartOffset,
                strip.GateOpeningDate,
                names,
                ShowAvailabilityControls: true);

            return View("~/Views/VolunteerTracking/_VolunteerHeatmap.cshtml", model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading build strip for user {UserId}", userId);
            return Content(string.Empty);
        }
    }
}
```

> `GetUserInfoAsync(Guid)` is on `IUserServiceRead` and `UserInfo.BurnerName` exists (used identically in `VolunteerTrackingController.Index`). Inject the `*Read` interface, matching `ShiftSignupsViewComponent`'s use of `ITeamServiceRead` — never the full write interface from a cross-section host.

- [ ] **Step 2: Build** — `dotnet build src/Humans.Web -v quiet`. Expected: success.
- [ ] **Step 3: Commit** — `git add ... && git commit -m "feat(profile): VolunteerBuildStripViewComponent"`

---

### Task 9: Wire into the profile + localization

**Files:**
- Modify: `src/Humans.Web/Views/Profile/Index.cshtml` (~line 84, inside the `!IsOwnProfile && CanViewShiftSignups` block)
- Modify: localization resx (find with: `ls src/Humans.Web/Resources/SharedResource*.resx`)

- [ ] **Step 1: Add the tag** immediately after the `<vc:shift-signups ... />` line:

```razor
<vc:volunteer-build-strip user-id="@Model.UserId" />
```

- [ ] **Step 2: Add resource keys** to **every** locale `.resx` in `src/Humans.Web/Resources/` — all six: `SharedResource.resx` (English), `.es`, `.ca`, `.de`, `.fr`, `.it` (per the i18n change-enforcement rule in `memory/`; `dotnet build` may fail on missing keys per existing tooling). Existing `VolTrack_Popover_*` / `VolTrack_Main_*` keys are already present in each, so follow their placement:

| Key | English | Spanish (draft — confirm with Peter) |
|---|---|---|
| `Profile_BuildStrip_Title` | `Build availability & tracking` | `Disponibilidad y seguimiento de montaje` |
| `VolTrack_Popover_MarkAvailable` | `Mark available` | `Marcar disponible` |
| `VolTrack_Popover_UnmarkAvailable` | `Unmark available` | `Quitar disponible` |
| `VolTrack_Msg_AvailabilitySet` | `Availability updated.` | `Disponibilidad actualizada.` |
| `VolTrack_Msg_AvailabilityCleared` | `Availability updated.` | `Disponibilidad actualizada.` |

> `ca`/`de`/`fr`/`it`: add the same keys. If you can't produce confident translations, copy the English value and flag for Peter in the PR — every key must exist in every file so the build/i18n check passes. Don't leave a key missing in any locale.

- [ ] **Step 3: Build** — `dotnet build Humans.slnx -v quiet`. Expected: success.
- [ ] **Step 4: Commit** — `git commit -am "feat(profile): show coordinator build strip + i18n keys"`

---

### Task 10: Full verification (build, test, run the app)

- [ ] **Step 1: Full build + test**

Run: `dotnet build Humans.slnx -v quiet && dotnet test Humans.slnx -v quiet`
Expected: build clean; all tests pass (incl. `ProfileArchitectureTests`, `ServiceBoundaryArchitectureTests` — cross-section compliance by construction).

- [ ] **Step 2: Manual verification** (CLAUDE.md: exercise the change, don't just trust tests). Use the `/run` or `/test-site` skill, or `dotnet run --project src/Humans.Web` with dev auth. As a coordinator/admin, open another volunteer's profile (`/Profile/{id}`):
  - The "Build availability & tracking" card appears (and does NOT on your own profile, and not for a non-coordinator).
  - Click a build-day cell → popover shows the availability toggle alongside camp-setup/day-off controls.
  - "Mark available" / "Unmark available" persists and returns you to the profile (not the tracking dashboard).
  - The Volunteer Tracking dashboard (`/Shifts/Dashboard/VolunteerTracking`) is unchanged (no availability toggle on its main heatmap; edits still return there).

- [ ] **Step 3: Confirm no stray files / clean status** — `git status` clean.

---

## Post-implementation

- [ ] Suggest a version bump per the project's release flow (this is a user-facing feature).
- [ ] Open a PR to `main` on the fork per the two-remote workflow (preview env at `{pr_id}.n.burn.camp`).
- [ ] Update the stale note in `src/Humans.Web/Views/.../WidgetGallery/Index.cshtml` (~line 1081) that claims the heatmap partials are "context-bound … cannot render in isolation" — no longer true (`VolunteerBuildStripViewComponent` renders `_VolunteerHeatmap` standalone with a `HeatmapPartialModel`). Grep for the text to find the exact line.
- [ ] Consider a `memory/` atom if any new project rule surfaced (e.g. "single-volunteer build strip is a ViewComponent rendering the shared `_VolunteerHeatmap` partial; coordinator availability edits POST to VolunteerTrackingController with returnUrl").
