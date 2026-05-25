# Early Entry Roster + Ticket-Stub Self-View Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface cross-source Early Entry (EE) — who holds it, from where, and as of when — on each holder's ticket stub and in a site-wide admin roster.

**Architecture:** Mirror the GDPR contributor fan-out. A read-only `IEarlyEntryProvider` is implemented by the two services that already own EE data (`CampService`, `VolunteerTrackingExportService`); an orchestrator (`EarlyEntryService`) injects `IEnumerable<IEarlyEntryProvider>` and assembles per-user results. A Singleton caching decorator (`CachingEarlyEntryService`, `TrackedCache`, no warmup) caches the per-user stub read and exposes a §15e `IEarlyEntryInvalidator` wired to the Camps + Shifts write paths.

**Tech Stack:** C# / .NET, NodaTime (`LocalDate`/`Instant`), EF Core (behind repositories only), xUnit + NSubstitute, ASP.NET Core MVC + Razor view components.

**Conventions:**
- Build/test: `dotnet build Humans.slnx -v quiet`, `dotnet test Humans.slnx -v quiet` (the `-v quiet` is required).
- Run a single test: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~EarlyEntryServiceTests"`.
- All work happens in the `early-entry-roster` worktree on branch `early-entry-roster`.
- Spec: `docs/superpowers/specs/2026-05-25-early-entry-roster-design.md`.

---

## File Structure

**New — contract + orchestrator (Application):**
- `src/Humans.Application/Interfaces/EarlyEntry/IEarlyEntryProvider.cs` — provider interface + `EarlyEntryGrant` record.
- `src/Humans.Application/Interfaces/EarlyEntry/IEarlyEntryService.cs` — orchestrator interface + `EarlyEntryRosterRow` + `UserEarlyEntry` records.
- `src/Humans.Application/Interfaces/EarlyEntry/IEarlyEntryInvalidator.cs` — §15e one-way invalidator.
- `src/Humans.Application/Services/EarlyEntry/EarlyEntryService.cs` — fan-out orchestrator.
- `src/Humans.Application/Services/Camps/CampEarlyEntryProjection.cs` — pure projection helper (testable).
- `src/Humans.Application/Services/Shifts/ShiftEarlyEntryProjection.cs` — pure projection helper (testable).

**New — caching decorator (Infrastructure):**
- `src/Humans.Infrastructure/Services/EarlyEntry/CachingEarlyEntryService.cs` — Singleton decorator + invalidator.

**New — web (roster):**
- `src/Humans.Web/Controllers/EarlyEntryRosterController.cs` — `/Shifts/Admin/EarlyEntry`.
- `src/Humans.Web/Models/EarlyEntry/EarlyEntryRosterViewModel.cs` — view model (name-resolved rows).
- `src/Humans.Web/Views/EarlyEntryRoster/Index.cshtml` — roster page.
- `src/Humans.Web/Extensions/Sections/EarlyEntrySectionExtensions.cs` — DI wiring.

**Modified:**
- `src/Humans.Application/Services/Camps/CampService.cs` — implement `IEarlyEntryProvider`; inject `IEarlyEntryInvalidator`; call it from `SetEarlyEntryAsync` + the removal cascade.
- `src/Humans.Application/Services/Shifts/VolunteerTrackingExportService.cs` — implement `IEarlyEntryProvider`; extract shared first-shift helper.
- `src/Humans.Application/Services/Shifts/ShiftSignupService.cs` — inject `IEarlyEntryInvalidator`; call it on build-shift confirm/bail.
- `src/Humans.Application/DTOs/TicketTransferDtos.cs` — add optional `EarlyEntryDate` to `TicketStubInfo`.
- `src/Humans.Web/ViewComponents/TicketHoldingsViewComponent.cs` — populate `EarlyEntryDate` for the holder.
- `src/Humans.Web/Views/Shared/Components/TicketStub/Default.cshtml` — render the EE line.
- `src/Humans.Web/Extensions/Sections/CampsSectionExtensions.cs` + `ShiftsSectionExtensions.cs` — register `IEarlyEntryProvider`.
- `src/Humans.Web/Program.cs` (or the section-registration aggregator) — call `AddEarlyEntrySection()`.
- `docs/sections/Shifts.md`, `docs/sections/Camps.md` — note the EE contributor.

**Tests:**
- `tests/Humans.Application.Tests/Services/EarlyEntry/EarlyEntryServiceTests.cs`
- `tests/Humans.Application.Tests/Services/EarlyEntry/CampEarlyEntryProjectionTests.cs`
- `tests/Humans.Application.Tests/Services/EarlyEntry/ShiftEarlyEntryProjectionTests.cs`
- `tests/Humans.Application.Tests/Services/EarlyEntry/CachingEarlyEntryServiceTests.cs`
- `tests/Humans.Web.Tests/Controllers/EarlyEntryRosterControllerTests.cs`
- `tests/Humans.Web.Tests/ViewComponents/TicketHoldingsViewComponentEarlyEntryTests.cs` (if a web-tests view-component pattern exists; otherwise assert via the controller/integration layer)

---

## Task 1: Contract types

**Files:**
- Create: `src/Humans.Application/Interfaces/EarlyEntry/IEarlyEntryProvider.cs`
- Create: `src/Humans.Application/Interfaces/EarlyEntry/IEarlyEntryService.cs`
- Create: `src/Humans.Application/Interfaces/EarlyEntry/IEarlyEntryInvalidator.cs`

These are pure declarations (no behavior), so no test in this task — they are exercised by Tasks 2–5.

- [ ] **Step 1: Create the provider contract**

`src/Humans.Application/Interfaces/EarlyEntry/IEarlyEntryProvider.cs`:

```csharp
using NodaTime;

namespace Humans.Application.Interfaces.EarlyEntry;

/// <summary>
/// Contributes the Early Entry grants this section owns for the active event.
/// Mirrors the GDPR <c>IUserDataContributor</c> fan-out: each section that owns
/// EE-relevant data implements this; <see cref="IEarlyEntryService"/> assembles
/// the cross-source view. Read-only. A section with nothing to contribute
/// (e.g. no EE start date configured) returns an empty list.
/// </summary>
public interface IEarlyEntryProvider
{
    Task<IReadOnlyList<EarlyEntryGrant>> GetEarlyEntriesAsync(CancellationToken ct);
}

/// <summary>
/// One EE grant: the user, the date they may enter, and a display label for the
/// source (e.g. "Camp: Flaming Lotus", "Shift: Flags").
/// </summary>
public sealed record EarlyEntryGrant(Guid UserId, LocalDate EntryDate, string Source);
```

- [ ] **Step 2: Create the orchestrator contract**

`src/Humans.Application/Interfaces/EarlyEntry/IEarlyEntryService.cs`:

```csharp
using Humans.Application.Interfaces;
using NodaTime;

namespace Humans.Application.Interfaces.EarlyEntry;

/// <summary>
/// Cross-source EE read orchestrator. Fans out over every
/// <see cref="IEarlyEntryProvider"/> and assembles per-user results. Owns no
/// tables (orchestrator per the hard rules).
/// </summary>
public interface IEarlyEntryService : IApplicationService
{
    /// <summary>All EE holders for the active event, one row per user, with the
    /// per-source breakdown and a wasted-slot flag. Live (uncached).</summary>
    Task<IReadOnlyList<EarlyEntryRosterRow>> GetRosterAsync(CancellationToken ct);

    /// <summary>The viewer's own EE, or null if they hold none. Cached.</summary>
    Task<UserEarlyEntry?> GetForUserAsync(Guid userId, CancellationToken ct);
}

/// <summary>One roster row: a user and every source that grants them EE.</summary>
public sealed record EarlyEntryRosterRow(
    Guid UserId,
    LocalDate EarliestEntryDate,
    IReadOnlyList<string> Sources,
    bool HasMultiple);

/// <summary>The earliest date a user may enter, plus the source label(s).</summary>
public sealed record UserEarlyEntry(LocalDate EarliestEntryDate, IReadOnlyList<string> Sources);
```

- [ ] **Step 3: Create the invalidator contract**

`src/Humans.Application/Interfaces/EarlyEntry/IEarlyEntryInvalidator.cs`:

```csharp
namespace Humans.Application.Interfaces.EarlyEntry;

/// <summary>
/// §15e one-way cache-staleness signal for the per-user EE cache. Implemented by
/// the caching decorator. EE is derived from camp grants and build-shift signups,
/// so the Camps and Shifts write paths inject this and evict the affected user
/// after their writes. Pure eviction (the cache has no warmup); the next read
/// lazy-reloads.
/// </summary>
public interface IEarlyEntryInvalidator
{
    void InvalidateUser(Guid userId);
}
```

- [ ] **Step 4: Build to verify the contracts compile**

Run: `dotnet build Humans.slnx -v quiet`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Interfaces/EarlyEntry/
git commit -m "feat(early-entry): add provider/orchestrator/invalidator contracts"
```

---

## Task 2: Camps EE projection (pure helper)

**Files:**
- Create: `src/Humans.Application/Services/Camps/CampEarlyEntryProjection.cs`
- Test: `tests/Humans.Application.Tests/Services/EarlyEntry/CampEarlyEntryProjectionTests.cs`

The projection is a pure function so it is unit-testable without constructing the 11-dependency `CampService`. `CampService.GetEarlyEntriesAsync` (Task 6) just feeds it repo reads.

- [ ] **Step 1: Write the failing test**

`tests/Humans.Application.Tests/Services/EarlyEntry/CampEarlyEntryProjectionTests.cs`:

```csharp
using Humans.Application.Services.Camps;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Services.EarlyEntry;

public class CampEarlyEntryProjectionTests
{
    private static readonly LocalDate Ee = new(2026, 7, 7);

    private static CampMember Member(Guid userId, CampMemberStatus status, bool ee) =>
        new() { Id = Guid.NewGuid(), UserId = userId, Status = status, HasEarlyEntry = ee };

    [Fact]
    public void Emits_one_grant_per_active_granted_member_with_camp_name_and_global_date()
    {
        var seasonA = Guid.NewGuid();
        var seasonB = Guid.NewGuid();
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();

        var membersBySeason = new Dictionary<Guid, IReadOnlyList<CampMember>>
        {
            [seasonA] = new[] { Member(u1, CampMemberStatus.Active, true) },
            [seasonB] = new[] { Member(u2, CampMemberStatus.Active, true) },
        };
        var seasonNames = new Dictionary<Guid, string> { [seasonA] = "Flaming Lotus", [seasonB] = "Flags" };

        var grants = CampEarlyEntryProjection.Project(Ee, membersBySeason, seasonNames);

        Assert.Equal(2, grants.Count);
        Assert.Contains(grants, g => g.UserId == u1 && g.EntryDate == Ee && g.Source == "Camp: Flaming Lotus");
        Assert.Contains(grants, g => g.UserId == u2 && g.EntryDate == Ee && g.Source == "Camp: Flags");
    }

    [Fact]
    public void Excludes_non_active_and_non_granted_members()
    {
        var season = Guid.NewGuid();
        var membersBySeason = new Dictionary<Guid, IReadOnlyList<CampMember>>
        {
            [season] = new[]
            {
                Member(Guid.NewGuid(), CampMemberStatus.Pending, true),  // not active
                Member(Guid.NewGuid(), CampMemberStatus.Active, false),  // not granted
            },
        };
        var seasonNames = new Dictionary<Guid, string> { [season] = "Flags" };

        var grants = CampEarlyEntryProjection.Project(Ee, membersBySeason, seasonNames);

        Assert.Empty(grants);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~CampEarlyEntryProjectionTests"`
Expected: FAIL — `CampEarlyEntryProjection` does not exist.

- [ ] **Step 3: Write the projection**

`src/Humans.Application/Services/Camps/CampEarlyEntryProjection.cs`:

```csharp
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.Camps;

/// <summary>
/// Pure projection from the camps' membership data to EE grants. The entry date
/// is the single global <c>CampSettings.EeStartDate</c>; only Active members with
/// <c>HasEarlyEntry</c> contribute. Source label is "Camp: {season name}".
/// </summary>
internal static class CampEarlyEntryProjection
{
    internal static IReadOnlyList<EarlyEntryGrant> Project(
        LocalDate eeStartDate,
        IReadOnlyDictionary<Guid, IReadOnlyList<CampMember>> membersBySeasonId,
        IReadOnlyDictionary<Guid, string> seasonNameById)
    {
        var grants = new List<EarlyEntryGrant>();
        foreach (var (seasonId, members) in membersBySeasonId)
        {
            var name = seasonNameById.GetValueOrDefault(seasonId, "Camp");
            foreach (var m in members)
            {
                if (m.Status != CampMemberStatus.Active || !m.HasEarlyEntry) continue;
                grants.Add(new EarlyEntryGrant(m.UserId, eeStartDate, $"Camp: {name}"));
            }
        }
        return grants;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~CampEarlyEntryProjectionTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Services/Camps/CampEarlyEntryProjection.cs tests/Humans.Application.Tests/Services/EarlyEntry/CampEarlyEntryProjectionTests.cs
git commit -m "feat(early-entry): camps EE projection helper"
```

---

## Task 3: Shifts EE projection (pure helper) + shared first-shift extraction

**Files:**
- Create: `src/Humans.Application/Services/Shifts/ShiftEarlyEntryProjection.cs`
- Modify: `src/Humans.Application/Services/Shifts/VolunteerTrackingExportService.cs:152-162` (replace `ComputeFirstShiftDay` with a shared `ComputeFirstShift` that also yields the team)
- Test: `tests/Humans.Application.Tests/Services/EarlyEntry/ShiftEarlyEntryProjectionTests.cs`

EE date = earliest confirmed build-shift day − 1; source = the team of that earliest shift. The window the provider fetches (build days only) is built in Task 6; this helper works on whatever rows it is given.

- [ ] **Step 1: Write the failing test**

`tests/Humans.Application.Tests/Services/EarlyEntry/ShiftEarlyEntryProjectionTests.cs`:

```csharp
using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Application.Services.Shifts;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Services.EarlyEntry;

public class ShiftEarlyEntryProjectionTests
{
    private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    private static Instant At(int year, int month, int day, int hour) =>
        Madrid.AtLeniently(new LocalDateTime(year, month, day, hour, 0)).ToInstant();

    [Fact]
    public void Entry_date_is_earliest_shift_day_minus_one_with_that_shifts_team()
    {
        var user = Guid.NewGuid();
        var teamWed = Guid.NewGuid();
        var teamFri = Guid.NewGuid();
        var rows = new[]
        {
            // Friday shift (later)
            new ConfirmedShiftRow(user, teamFri, At(2026, 7, 3, 9), At(2026, 7, 3, 17)),
            // Wednesday shift (earliest) — this drives the EE
            new ConfirmedShiftRow(user, teamWed, At(2026, 7, 1, 9), At(2026, 7, 1, 17)),
        };
        var teamNames = new Dictionary<Guid, string> { [teamWed] = "Flags", [teamFri] = "Power" };

        var grants = ShiftEarlyEntryProjection.Project(rows, Madrid, teamNames);

        var g = Assert.Single(grants);
        Assert.Equal(user, g.UserId);
        Assert.Equal(new LocalDate(2026, 6, 30), g.EntryDate); // Wed 1 Jul − 1
        Assert.Equal("Shift: Flags", g.Source);
    }

    [Fact]
    public void Empty_rows_yield_no_grants()
    {
        var grants = ShiftEarlyEntryProjection.Project(
            Array.Empty<ConfirmedShiftRow>(), Madrid, new Dictionary<Guid, string>());
        Assert.Empty(grants);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~ShiftEarlyEntryProjectionTests"`
Expected: FAIL — `ShiftEarlyEntryProjection` does not exist.

- [ ] **Step 3: Write the projection + shared helper**

`src/Humans.Application/Services/Shifts/ShiftEarlyEntryProjection.cs`:

```csharp
using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Application.Interfaces.EarlyEntry;
using NodaTime;

namespace Humans.Application.Services.Shifts;

/// <summary>
/// Pure projection from confirmed build-shift rows to EE grants. Per user, the
/// earliest local shift day drives the grant: entry date = that day − 1, source
/// = "Shift: {team of that earliest shift}". Callers pass only build-period rows.
/// </summary>
internal static class ShiftEarlyEntryProjection
{
    internal static IReadOnlyList<EarlyEntryGrant> Project(
        IReadOnlyList<ConfirmedShiftRow> rows,
        DateTimeZone zone,
        IReadOnlyDictionary<Guid, string> teamNames)
    {
        // userId -> (earliest local day, team of that earliest shift)
        var earliest = new Dictionary<Guid, (LocalDate day, Guid teamId)>();
        foreach (var r in rows)
        {
            var day = r.StartsAtUtc.InZone(zone).LocalDateTime.Date;
            if (!earliest.TryGetValue(r.UserId, out var cur) || day < cur.day)
                earliest[r.UserId] = (day, r.TeamId);
        }

        var grants = new List<EarlyEntryGrant>(earliest.Count);
        foreach (var (userId, (day, teamId)) in earliest)
        {
            var team = teamNames.GetValueOrDefault(teamId, "shift");
            grants.Add(new EarlyEntryGrant(userId, day.PlusDays(-1), $"Shift: {team}"));
        }
        return grants;
    }
}
```

Now extract the shared first-shift step in `VolunteerTrackingExportService` so `BuildAsync` and the provider (Task 6) compute it once. Replace the existing `ComputeFirstShiftDay` (currently `src/Humans.Application/Services/Shifts/VolunteerTrackingExportService.cs:152-162`):

```csharp
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
```

with a version layered over the shared `ShiftEarlyEntryProjection` conversion, keeping `BuildAsync`'s `firstShiftDay[userId]` usage identical:

```csharp
    private static Dictionary<Guid, LocalDate> ComputeFirstShiftDay(IReadOnlyList<ConfirmedShiftRow> shifts, DateTimeZone zone) =>
        ShiftEarlyEntryProjection.FirstShiftDayByUser(shifts, zone);
```

and add the shared method to `ShiftEarlyEntryProjection` (so both paths share the local-date conversion, satisfying the spec's consolidation intent):

```csharp
    /// <summary>Earliest local shift day per user. Shared by the XLSX export and the EE provider.</summary>
    internal static Dictionary<Guid, LocalDate> FirstShiftDayByUser(
        IReadOnlyList<ConfirmedShiftRow> rows, DateTimeZone zone)
    {
        var first = new Dictionary<Guid, LocalDate>();
        foreach (var r in rows)
        {
            var day = r.StartsAtUtc.InZone(zone).LocalDateTime.Date;
            if (!first.TryGetValue(r.UserId, out var existing) || day < existing)
                first[r.UserId] = day;
        }
        return first;
    }
```

- [ ] **Step 4: Run the projection tests + the existing export tests to verify parity**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~ShiftEarlyEntryProjectionTests"`
Expected: PASS (2 tests).

Run the export regression guard:
`dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~VolunteerTrackingExport"`
Expected: PASS — `BuildAsync` output unchanged after the extraction.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Services/Shifts/ShiftEarlyEntryProjection.cs src/Humans.Application/Services/Shifts/VolunteerTrackingExportService.cs tests/Humans.Application.Tests/Services/EarlyEntry/ShiftEarlyEntryProjectionTests.cs
git commit -m "feat(early-entry): shifts EE projection + shared first-shift helper"
```

---

## Task 4: Orchestrator (`EarlyEntryService`)

**Files:**
- Create: `src/Humans.Application/Services/EarlyEntry/EarlyEntryService.cs`
- Test: `tests/Humans.Application.Tests/Services/EarlyEntry/EarlyEntryServiceTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/Humans.Application.Tests/Services/EarlyEntry/EarlyEntryServiceTests.cs`:

```csharp
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Services.EarlyEntry;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.EarlyEntry;

public class EarlyEntryServiceTests
{
    private static IEarlyEntryProvider Provider(params EarlyEntryGrant[] grants)
    {
        var p = Substitute.For<IEarlyEntryProvider>();
        p.GetEarlyEntriesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EarlyEntryGrant>>(grants));
        return p;
    }

    private static EarlyEntryService Service(params IEarlyEntryProvider[] providers) => new(providers);

    [Fact]
    public async Task Roster_groups_by_user_takes_earliest_date_lists_sources_and_flags_multiple()
    {
        var user = Guid.NewGuid();
        var camp = Provider(new EarlyEntryGrant(user, new LocalDate(2026, 7, 7), "Camp: Flags"));
        var shift = Provider(new EarlyEntryGrant(user, new LocalDate(2026, 7, 1), "Shift: Power"));

        var rows = await Service(camp, shift).GetRosterAsync(CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(user, row.UserId);
        Assert.Equal(new LocalDate(2026, 7, 1), row.EarliestEntryDate);
        Assert.True(row.HasMultiple);
        Assert.Equal(2, row.Sources.Count);
        Assert.Contains("Camp: Flags", row.Sources);
        Assert.Contains("Shift: Power", row.Sources);
    }

    [Fact]
    public async Task Single_source_user_is_not_flagged_multiple()
    {
        var user = Guid.NewGuid();
        var rows = await Service(Provider(new EarlyEntryGrant(user, new LocalDate(2026, 7, 5), "Camp: Flags")))
            .GetRosterAsync(CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.False(row.HasMultiple);
    }

    [Fact]
    public async Task GetForUser_returns_earliest_with_sources_or_null()
    {
        var user = Guid.NewGuid();
        var svc = Service(
            Provider(new EarlyEntryGrant(user, new LocalDate(2026, 7, 7), "Camp: Flags")),
            Provider(new EarlyEntryGrant(user, new LocalDate(2026, 7, 1), "Shift: Power")));

        var hit = await svc.GetForUserAsync(user, CancellationToken.None);
        Assert.NotNull(hit);
        Assert.Equal(new LocalDate(2026, 7, 1), hit!.EarliestEntryDate);
        Assert.Equal(2, hit.Sources.Count);

        var miss = await svc.GetForUserAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Null(miss);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~EarlyEntryServiceTests"`
Expected: FAIL — `EarlyEntryService` does not exist.

- [ ] **Step 3: Write the orchestrator**

`src/Humans.Application/Services/EarlyEntry/EarlyEntryService.cs`:

```csharp
using Humans.Application.Interfaces.EarlyEntry;
using NodaTime;

namespace Humans.Application.Services.EarlyEntry;

/// <summary>
/// Fans out over every <see cref="IEarlyEntryProvider"/> and assembles per-user
/// EE results. Sequential, not Task.WhenAll: providers share the scoped
/// HumansDbContext, which is not thread-safe (same reason GdprExportService is
/// sequential). Owns no repository.
/// </summary>
public sealed class EarlyEntryService(IEnumerable<IEarlyEntryProvider> providers) : IEarlyEntryService
{
    public async Task<IReadOnlyList<EarlyEntryRosterRow>> GetRosterAsync(CancellationToken ct)
    {
        var all = await GatherAsync(ct);
        return all
            .GroupBy(g => g.UserId)
            .Select(grp =>
            {
                var sources = grp.Select(g => g.Source).Distinct().ToList();
                return new EarlyEntryRosterRow(
                    grp.Key,
                    grp.Min(g => g.EntryDate),
                    sources,
                    sources.Count > 1);
            })
            .ToList();
    }

    public async Task<UserEarlyEntry?> GetForUserAsync(Guid userId, CancellationToken ct)
    {
        var all = await GatherAsync(ct);
        var mine = all.Where(g => g.UserId == userId).ToList();
        if (mine.Count == 0) return null;
        return new UserEarlyEntry(
            mine.Min(g => g.EntryDate),
            mine.Select(g => g.Source).Distinct().ToList());
    }

    private async Task<List<EarlyEntryGrant>> GatherAsync(CancellationToken ct)
    {
        var all = new List<EarlyEntryGrant>();
        foreach (var provider in providers)
            all.AddRange(await provider.GetEarlyEntriesAsync(ct));
        return all;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~EarlyEntryServiceTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Services/EarlyEntry/ tests/Humans.Application.Tests/Services/EarlyEntry/EarlyEntryServiceTests.cs
git commit -m "feat(early-entry): cross-source orchestrator"
```

---

## Task 5: Caching decorator + invalidator

**Files:**
- Create: `src/Humans.Infrastructure/Services/EarlyEntry/CachingEarlyEntryService.cs`
- Test: `tests/Humans.Application.Tests/Services/EarlyEntry/CachingEarlyEntryServiceTests.cs`

The decorator caches `GetForUserAsync` (including negatives) and passes `GetRosterAsync` straight through. It uses manual `TryGet`/`Set` because `TrackedCache.GetAsync` does not cache nulls.

- [ ] **Step 1: Write the failing test**

`tests/Humans.Application.Tests/Services/EarlyEntry/CachingEarlyEntryServiceTests.cs`:

```csharp
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Infrastructure.Services.EarlyEntry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.EarlyEntry;

public class CachingEarlyEntryServiceTests
{
    // Builds a decorator whose keyed-inner IEarlyEntryService is the supplied substitute.
    private static (CachingEarlyEntryService cache, IEarlyEntryService inner) Build()
    {
        var inner = Substitute.For<IEarlyEntryService>();
        var services = new ServiceCollection();
        services.AddKeyedScoped<IEarlyEntryService>(
            CachingEarlyEntryService.InnerServiceKey, (_, _) => inner);
        var sp = services.BuildServiceProvider();
        var cache = new CachingEarlyEntryService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingEarlyEntryService>.Instance);
        return (cache, inner);
    }

    [Fact]
    public async Task Second_GetForUser_is_served_from_cache()
    {
        var (cache, inner) = Build();
        var user = Guid.NewGuid();
        inner.GetForUserAsync(user, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserEarlyEntry?>(
                new UserEarlyEntry(new LocalDate(2026, 7, 1), new[] { "Shift: Power" })));

        var first = await cache.GetForUserAsync(user, CancellationToken.None);
        var second = await cache.GetForUserAsync(user, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        await inner.Received(1).GetForUserAsync(user, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Negative_result_is_cached()
    {
        var (cache, inner) = Build();
        var user = Guid.NewGuid();
        inner.GetForUserAsync(user, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserEarlyEntry?>(null));

        Assert.Null(await cache.GetForUserAsync(user, CancellationToken.None));
        Assert.Null(await cache.GetForUserAsync(user, CancellationToken.None));

        await inner.Received(1).GetForUserAsync(user, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateUser_forces_a_reload()
    {
        var (cache, inner) = Build();
        var user = Guid.NewGuid();
        inner.GetForUserAsync(user, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserEarlyEntry?>(null));

        await cache.GetForUserAsync(user, CancellationToken.None);
        cache.InvalidateUser(user);
        await cache.GetForUserAsync(user, CancellationToken.None);

        await inner.Received(2).GetForUserAsync(user, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetRoster_always_delegates_to_inner()
    {
        var (cache, inner) = Build();
        inner.GetRosterAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EarlyEntryRosterRow>>(Array.Empty<EarlyEntryRosterRow>()));

        await cache.GetRosterAsync(CancellationToken.None);
        await cache.GetRosterAsync(CancellationToken.None);

        await inner.Received(2).GetRosterAsync(Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~CachingEarlyEntryServiceTests"`
Expected: FAIL — `CachingEarlyEntryService` does not exist.

- [ ] **Step 3: Write the decorator**

`src/Humans.Infrastructure/Services/EarlyEntry/CachingEarlyEntryService.cs`:

```csharp
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.EarlyEntry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.EarlyEntry;

/// <summary>
/// Singleton caching decorator for <see cref="IEarlyEntryService"/>. Caches the
/// per-user stub read (<see cref="GetForUserAsync"/>), including the negative
/// (no-EE) result so the no-EE majority does not re-fan-out on every render.
/// <see cref="GetRosterAsync"/> always delegates to the inner service (the admin
/// roster must see live data). No startup warmup — cold-loaded on first read.
/// EE is derived, so external write paths evict via <see cref="IEarlyEntryInvalidator"/>.
/// </summary>
public sealed class CachingEarlyEntryService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingEarlyEntryService> logger)
    : TrackedCache<Guid, UserEarlyEntry?>("EarlyEntry.UserEarlyEntry", warmOnStartup: false, logger),
        IEarlyEntryService, IEarlyEntryInvalidator
{
    public const string InnerServiceKey = "early-entry-inner";

    public async Task<UserEarlyEntry?> GetForUserAsync(Guid userId, CancellationToken ct)
    {
        if (TryGet(userId, out var cached)) return cached; // cached may be null (negative)

        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IEarlyEntryService>(InnerServiceKey);
        var result = await inner.GetForUserAsync(userId, ct);
        Set(userId, result);
        return result;
    }

    public async Task<IReadOnlyList<EarlyEntryRosterRow>> GetRosterAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IEarlyEntryService>(InnerServiceKey);
        return await inner.GetRosterAsync(ct);
    }

    public void InvalidateUser(Guid userId) => Invalidate(userId);
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~CachingEarlyEntryServiceTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Services/EarlyEntry/ tests/Humans.Application.Tests/Services/EarlyEntry/CachingEarlyEntryServiceTests.cs
git commit -m "feat(early-entry): caching decorator + invalidator"
```

---

## Task 6: Implement the providers on the owning services

**Files:**
- Modify: `src/Humans.Application/Services/Camps/CampService.cs` (add `IEarlyEntryProvider` + `GetEarlyEntriesAsync`)
- Modify: `src/Humans.Application/Services/Shifts/VolunteerTrackingExportService.cs` (add `IEarlyEntryProvider` + `GetEarlyEntriesAsync`)

No new tests here — the projection helpers (Tasks 2–3) are already tested; these methods are thin repo-read wiring verified by the build and the integration-level roster test (Task 8). Keep the change to wiring only.

- [ ] **Step 1: Camps — declare the interface**

In `src/Humans.Application/Services/Camps/CampService.cs:22`, add `IEarlyEntryProvider` to the class declaration:

```csharp
public sealed class CampService : ICampService, IUserDataContributor, IUserMerge, IEarlyEntryProvider
```

Add the using at the top with the other interface usings:

```csharp
using Humans.Application.Interfaces.EarlyEntry;
```

- [ ] **Step 2: Camps — implement `GetEarlyEntriesAsync`**

Add this method to `CampService` (next to the other EE methods, after `SetEarlyEntryAsync`):

```csharp
    public async Task<IReadOnlyList<EarlyEntryGrant>> GetEarlyEntriesAsync(CancellationToken ct)
    {
        var settings = await _repo.GetSettingsReadOnlyAsync(ct);
        if (settings?.EeStartDate is not { } eeStartDate)
            return [];

        var year = settings.PublicYear;
        var membersBySeason = await _repo.GetMembersForYearAsync(year, ct);
        var seasonDisplay = await _repo.GetSeasonDisplayDataForYearAsync(year, ct);
        var seasonNames = seasonDisplay.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Name);

        return CampEarlyEntryProjection.Project(eeStartDate, membersBySeason, seasonNames);
    }
```

- [ ] **Step 3: Shifts — declare the interface**

In `src/Humans.Application/Services/Shifts/VolunteerTrackingExportService.cs:16-20`, add `IEarlyEntryProvider` to the declaration:

```csharp
public sealed class VolunteerTrackingExportService(
    IVolunteerTrackingRepository repository,
    IShiftManagementService shiftManagementService,
    IUserServiceRead userService)
    : IVolunteerTrackingExportService, IEarlyEntryProvider
```

Add the using:

```csharp
using Humans.Application.Interfaces.EarlyEntry;
```

- [ ] **Step 4: Shifts — implement `GetEarlyEntriesAsync`**

Add to `VolunteerTrackingExportService` (the build window is `[gate + BuildStartOffset .. gate − 1]`, so only setup/build shifts are fetched — event/strike are never touched):

```csharp
    public async Task<IReadOnlyList<EarlyEntryGrant>> GetEarlyEntriesAsync(CancellationToken ct)
    {
        var es = await _shiftManagementService.GetActiveAsync();
        if (es is null) return [];

        var start = es.GateOpeningDate.PlusDays(es.BuildStartOffset);
        var end = es.GateOpeningDate.PlusDays(-1);
        var rows = await _repository.GetConfirmedShiftsInRangeAsync(es.Id, start, end, departmentId: null, ct);
        if (rows.Count == 0) return [];

        var depts = await _shiftManagementService.GetDepartmentsWithRotasAsync(es.Id);
        var teamNames = depts
            .GroupBy(d => d.TeamId)
            .ToDictionary(g => g.Key, g => g.First().TeamName);

        var zone = DateTimeZoneProviders.Tzdb[es.TimeZoneId];
        return ShiftEarlyEntryProjection.Project(rows, zone, teamNames);
    }
```

- [ ] **Step 5: Build to verify both providers compile**

Run: `dotnet build Humans.slnx -v quiet`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Services/Camps/CampService.cs src/Humans.Application/Services/Shifts/VolunteerTrackingExportService.cs
git commit -m "feat(early-entry): implement providers on CampService + VolunteerTrackingExportService"
```

---

## Task 7: DI wiring + invalidation hooks

**Files:**
- Create: `src/Humans.Web/Extensions/Sections/EarlyEntrySectionExtensions.cs`
- Modify: `src/Humans.Web/Extensions/Sections/CampsSectionExtensions.cs`
- Modify: `src/Humans.Web/Extensions/Sections/ShiftsSectionExtensions.cs`
- Modify: `src/Humans.Web/Program.cs` (call `AddEarlyEntrySection`)
- Modify: `src/Humans.Application/Services/Camps/CampService.cs` (inject + call invalidator)
- Modify: `src/Humans.Application/Services/Shifts/ShiftSignupService.cs` (inject + call invalidator)

- [ ] **Step 1: Create the EarlyEntry section extension**

`src/Humans.Web/Extensions/Sections/EarlyEntrySectionExtensions.cs` (mirrors the keyed-inner + Singleton-decorator shape from `CampsSectionExtensions`):

```csharp
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Infrastructure.Services.EarlyEntry;
using EarlyEntryOrchestrator = Humans.Application.Services.EarlyEntry.EarlyEntryService;

namespace Humans.Web.Extensions.Sections;

internal static class EarlyEntrySectionExtensions
{
    internal static IServiceCollection AddEarlyEntrySection(this IServiceCollection services)
    {
        // Orchestrator (inner) — Scoped + keyed so the Singleton decorator resolves it per-call.
        services.AddKeyedScoped<IEarlyEntryService, EarlyEntryOrchestrator>(
            CachingEarlyEntryService.InnerServiceKey);

        // Singleton decorator; same instance backs read + invalidator (§15e).
        services.AddSingleton<CachingEarlyEntryService>();
        services.AddSingleton<IEarlyEntryService>(sp => sp.GetRequiredService<CachingEarlyEntryService>());
        services.AddSingleton<IEarlyEntryInvalidator>(sp => sp.GetRequiredService<CachingEarlyEntryService>());
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingEarlyEntryService>());

        return services;
    }
}
```

- [ ] **Step 2: Register the providers in the owning sections**

In `src/Humans.Web/Extensions/Sections/CampsSectionExtensions.cs`, after the existing `IUserDataContributor` registration (line ~30), add:

```csharp
        // EE contributor on the inner — same fan-out pattern as GDPR.
        services.AddScoped<IEarlyEntryProvider>(sp => sp.GetRequiredService<CampsCampService>());
```

Add the using at the top:

```csharp
using Humans.Application.Interfaces.EarlyEntry;
```

In `src/Humans.Web/Extensions/Sections/ShiftsSectionExtensions.cs`, find the `VolunteerTrackingExportService` registration and register it as an `IEarlyEntryProvider` too. Add this line next to it (the concrete type is `Humans.Application.Services.Shifts.VolunteerTrackingExportService`; add a `using` or use the fully-qualified name to match the file's style):

```csharp
        services.AddScoped<IEarlyEntryProvider>(sp =>
            sp.GetRequiredService<Humans.Application.Services.Shifts.VolunteerTrackingExportService>());
```

> If `VolunteerTrackingExportService` is currently registered only via its interface (`IVolunteerTrackingExportService`) and not as its concrete type, add a concrete registration first:
> ```csharp
> services.AddScoped<Humans.Application.Services.Shifts.VolunteerTrackingExportService>();
> services.AddScoped<IVolunteerTrackingExportService>(sp =>
>     sp.GetRequiredService<Humans.Application.Services.Shifts.VolunteerTrackingExportService>());
> services.AddScoped<IEarlyEntryProvider>(sp =>
>     sp.GetRequiredService<Humans.Application.Services.Shifts.VolunteerTrackingExportService>());
> ```
> (Open `ShiftsSectionExtensions.cs`, locate the existing line, and adapt to whichever shape is present.)

- [ ] **Step 3: Call `AddEarlyEntrySection` in startup**

In `src/Humans.Web/Program.cs`, find where the other `Add*Section()` calls are chained (e.g. `AddCampsSection()`, `AddUsersSection()`) and add:

```csharp
        .AddEarlyEntrySection()
```

(Place it after `AddCampsSection()` and the Shifts section so the providers are registered first; ordering does not affect `IEnumerable<IEarlyEntryProvider>` resolution, but keep it tidy.)

- [ ] **Step 4: Inject the invalidator into Camps writes**

In `src/Humans.Application/Services/Camps/CampService.cs`, add a constructor parameter `IEarlyEntryInvalidator earlyEntryInvalidator` and field `_earlyEntryInvalidator` (follow the existing ctor-assignment style at lines 41-65). Then:

In `SetEarlyEntryAsync` (after the successful `await _repo.SaveMemberAsync(member, ...)` + audit write, before returning the success outcome), add:

```csharp
        _earlyEntryInvalidator.InvalidateUser(member.UserId);
```

In the member-removal cascade (the method that sets `member.HasEarlyEntry = false` at line ~1389, after `SaveMemberAsync`), add:

```csharp
        _earlyEntryInvalidator.InvalidateUser(member.UserId);
```

- [ ] **Step 5: Inject the invalidator into Shifts confirm/bail**

In `src/Humans.Application/Services/Shifts/ShiftSignupService.cs`, add an `IEarlyEntryInvalidator` constructor dependency (match the existing primary-constructor or classic-ctor style of that file). After a build-shift signup is confirmed or bailed (the `signup.Confirm(...)` and `signup.Bail(...)` paths where `signup.Shift.IsEarlyEntry` is true), call:

```csharp
        if (signup.Shift.IsEarlyEntry)
            _earlyEntryInvalidator.InvalidateUser(signup.UserId);
```

Apply it in each of the confirm and bail methods that already branch on `signup.Shift.IsEarlyEntry` (around `ShiftSignupService.cs:69`, `:140`, `:216`, `:726`, `:850`). Use the `signup`'s user id (`signup.UserId`); for the block-bail path that operates on a list, call it once per affected `signup`.

Add the using:

```csharp
using Humans.Application.Interfaces.EarlyEntry;
```

- [ ] **Step 6: Build + full test run**

Run: `dotnet build Humans.slnx -v quiet`
Expected: Build succeeded.

Run: `dotnet test Humans.slnx -v quiet`
Expected: PASS — all existing tests plus the new EE tests green. (Watch for DI-resolution integration tests that construct the full container; they verify the new registrations resolve.)

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Web/Extensions/Sections/ src/Humans.Web/Program.cs src/Humans.Application/Services/Camps/CampService.cs src/Humans.Application/Services/Shifts/ShiftSignupService.cs
git commit -m "feat(early-entry): DI wiring + Camps/Shifts invalidation hooks"
```

---

## Task 8: Roster page (`/Shifts/Admin/EarlyEntry`)

**Files:**
- Create: `src/Humans.Web/Models/EarlyEntry/EarlyEntryRosterViewModel.cs`
- Create: `src/Humans.Web/Controllers/EarlyEntryRosterController.cs`
- Create: `src/Humans.Web/Views/EarlyEntryRoster/Index.cshtml`
- Test: `tests/Humans.Web.Tests/Controllers/EarlyEntryRosterControllerTests.cs`

The controller resolves display names (controllers do formatting/sorting per the hard rules; the orchestrator stays name-free) and flags multi-source rows.

- [ ] **Step 1: Write the view model**

`src/Humans.Web/Models/EarlyEntry/EarlyEntryRosterViewModel.cs`:

```csharp
using NodaTime;

namespace Humans.Web.Models.EarlyEntry;

public sealed record EarlyEntryRosterViewModel(IReadOnlyList<EarlyEntryRosterRowVm> Rows);

public sealed record EarlyEntryRosterRowVm(
    string DisplayName,
    LocalDate EarliestEntryDate,
    IReadOnlyList<string> Sources,
    bool HasMultiple);
```

- [ ] **Step 2: Write the failing controller test**

`tests/Humans.Web.Tests/Controllers/EarlyEntryRosterControllerTests.cs`:

```csharp
using Humans.Application;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Users;
using Humans.Web.Controllers;
using Humans.Web.Models.EarlyEntry;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers;

public class EarlyEntryRosterControllerTests
{
    [Fact]
    public async Task Index_resolves_names_and_preserves_multiple_flag()
    {
        var user = Guid.NewGuid();
        var ee = Substitute.For<IEarlyEntryService>();
        ee.GetRosterAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<EarlyEntryRosterRow>>(new[]
            {
                new EarlyEntryRosterRow(user, new LocalDate(2026, 7, 1),
                    new[] { "Camp: Flags", "Shift: Power" }, HasMultiple: true),
            }));

        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(user, Arg.Any<CancellationToken>())
            .Returns(new UserInfo { Id = user, BurnerName = "Spanner" });

        var controller = new EarlyEntryRosterController(ee, users);

        var result = await controller.Index(CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<EarlyEntryRosterViewModel>(view.Model);
        var row = Assert.Single(vm.Rows);
        Assert.Equal("Spanner", row.DisplayName);
        Assert.True(row.HasMultiple);
        Assert.Equal(2, row.Sources.Count);
    }
}
```

> Note: `UserInfo` construction in the test must match the real `UserInfo` shape (it is a class with `Id` + `BurnerName` among other members — see `src/Humans.Application/UserInfo.cs`). If `UserInfo` requires more init members, set only what compiles; the test only reads `BurnerName`. Follow the existing pattern in `tests/Humans.Application.Tests/Infrastructure/UserInfoStubHelpers.cs` if a helper exists.

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~EarlyEntryRosterControllerTests"`
Expected: FAIL — `EarlyEntryRosterController` does not exist.

- [ ] **Step 4: Write the controller**

`src/Humans.Web/Controllers/EarlyEntryRosterController.cs`:

```csharp
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Users;
using Humans.Web.Authorization;
using Humans.Web.Models.EarlyEntry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// Site-wide EE roster. Interim home under the Shifts admin area; reuses the
/// shift-dashboard authorization gate. Rows with EE from more than one source
/// are flagged so a coordinator can free the redundant (usually camp) site slot.
/// </summary>
[Route("Shifts/Admin/EarlyEntry")]
[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]
public sealed class EarlyEntryRosterController(
    IEarlyEntryService earlyEntryService,
    IUserServiceRead userService) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var rows = await earlyEntryService.GetRosterAsync(ct);

        var vmRows = new List<EarlyEntryRosterRowVm>(rows.Count);
        foreach (var r in rows)
        {
            var info = await userService.GetUserInfoAsync(r.UserId, ct);
            vmRows.Add(new EarlyEntryRosterRowVm(
                info?.BurnerName ?? "(unknown)",
                r.EarliestEntryDate,
                r.Sources,
                r.HasMultiple));
        }

        var ordered = vmRows
            .OrderBy(r => r.EarliestEntryDate)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return View(new EarlyEntryRosterViewModel(ordered));
    }
}
```

> If `EarlyEntryRosterControllerTests` constructs the controller directly, the base `Controller` is fine. If the project's controllers conventionally inherit `HumansControllerBase(userService)` (as `VolunteerTrackingController` does) and that base provides shared helpers, switch the base and pass `userService` to it — but keep the explicit `IUserServiceRead` field used above.

- [ ] **Step 5: Write the view**

`src/Humans.Web/Views/EarlyEntryRoster/Index.cshtml`:

```cshtml
@model Humans.Web.Models.EarlyEntry.EarlyEntryRosterViewModel
@{
    ViewData["Title"] = "Early Entry Roster";
}
<h1>Early Entry Roster</h1>
<p class="text-muted">Everyone granted early entry, from camps and build shifts. Rows flagged
    <span class="badge bg-warning text-dark">multiple</span> hold EE from more than one source —
    one slot is redundant and can be reallocated.</p>

@if (Model.Rows.Count == 0)
{
    <p>No early entry grants for the active event.</p>
}
else
{
    <table class="table table-sm">
        <thead>
            <tr><th>Human</th><th>Enters</th><th>Source(s)</th><th></th></tr>
        </thead>
        <tbody>
        @foreach (var row in Model.Rows)
        {
            <tr class="@(row.HasMultiple ? "table-warning" : "")">
                <td>@row.DisplayName</td>
                <td>@row.EarliestEntryDate.ToString("ddd d MMM", null)</td>
                <td>@string.Join(", ", row.Sources)</td>
                <td>@if (row.HasMultiple) { <span class="badge bg-warning text-dark">multiple</span> }</td>
            </tr>
        }
        </tbody>
    </table>
}
```

- [ ] **Step 6: Run the test + build**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~EarlyEntryRosterControllerTests"`
Expected: PASS.

Run: `dotnet build Humans.slnx -v quiet`
Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Web/Controllers/EarlyEntryRosterController.cs src/Humans.Web/Models/EarlyEntry/ src/Humans.Web/Views/EarlyEntryRoster/ tests/Humans.Web.Tests/Controllers/EarlyEntryRosterControllerTests.cs
git commit -m "feat(early-entry): admin roster page at /Shifts/Admin/EarlyEntry"
```

---

## Task 9: Ticket-stub self-view

**Files:**
- Modify: `src/Humans.Application/DTOs/TicketTransferDtos.cs:88-94` (add optional `EarlyEntryDate`)
- Modify: `src/Humans.Web/ViewComponents/TicketHoldingsViewComponent.cs` (populate it for the holder)
- Modify: `src/Humans.Web/Views/Shared/Components/TicketStub/Default.cshtml` (render the line)

The stub renders in several contexts; EE is shown only for the viewer's own held tickets. `TicketHoldingsViewComponent` renders exactly that (the current user's holdings), so it is the single injection point. Other build sites (transfer wizard, dashboard inline lambdas) keep passing the default `null` → no EE shown there.

- [ ] **Step 1: Add the optional field to `TicketStubInfo`**

In `src/Humans.Application/DTOs/TicketTransferDtos.cs`, change the `TicketStubInfo` record (lines 88-94) to add a trailing optional parameter (non-breaking for all positional callers):

```csharp
public sealed record TicketStubInfo(
    string AttendeeName,
    string? AttendeeEmail,
    string VendorTicketId,
    TicketAttendeeStatus Status,
    bool HasPendingTransfer,
    Guid? PendingTransferRequestId,
    LocalDate? EarlyEntryDate = null);
```

`NodaTime` is already imported at the top of the file (`using NodaTime;`).

- [ ] **Step 2: Populate it in the holdings view component**

In `src/Humans.Web/ViewComponents/TicketHoldingsViewComponent.cs`, inject the EE service and set the date on every stub (the holder is `userId`, so one lookup covers all their stubs):

```csharp
using Humans.Application.DTOs;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Tickets;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public sealed class TicketHoldingsViewComponent(
    ITicketServiceRead queryService,
    ITicketTransferService transferService,
    IEarlyEntryService earlyEntryService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, bool showEmpty = false)
    {
        var holdings = await queryService.GetUserTicketHoldingsAsync(userId);

        if (!showEmpty && holdings.OrderCount == 0 && holdings.Tickets.Count == 0)
            return Content(string.Empty);

        var pendingAttendeeIds = (await transferService.GetMyAttendeesAsync(userId))
            .Where(a => a.HasPendingOutgoingTransfer)
            .Select(a => a.AttendeeId)
            .ToHashSet();

        // The holder's own EE (earliest date across sources), shown on each of their stubs.
        var earlyEntry = await earlyEntryService.GetForUserAsync(userId, HttpContext.RequestAborted);

        var stubs = holdings.Tickets
            .Select(t => new TicketStubInfo(
                t.AttendeeName,
                t.AttendeeEmail,
                t.VendorTicketId,
                t.Status,
                pendingAttendeeIds.Contains(t.AttendeeId),
                PendingTransferRequestId: null,
                EarlyEntryDate: earlyEntry?.EarliestEntryDate))
            .ToList();

        return View(new TicketHoldingsViewModel(holdings.OrderCount, stubs));
    }
}

public sealed record TicketHoldingsViewModel(int OrderCount, IReadOnlyList<TicketStubInfo> Tickets);
```

- [ ] **Step 3: Render the EE line on the stub**

In `src/Humans.Web/Views/Shared/Components/TicketStub/Default.cshtml`, inside `.ticket-stub-main` (after the check-in / void badge block, before the closing `</div>` at line 55), add:

```cshtml
        @if (Model.EarlyEntryDate is { } ee)
        {
            <div class="ticket-stub-badge" style="background:#5b4636; color:#fff; margin-top:8px;">
                <i class="fa-solid fa-door-open me-1"></i>Early entry @ee.ToString("ddd d MMM", null)
            </div>
        }
```

- [ ] **Step 4: Build to verify all stub call sites still compile**

Run: `dotnet build Humans.slnx -v quiet`
Expected: Build succeeded — the new `TicketStubInfo` parameter is optional, so the transfer wizard, dashboard, and widget-gallery call sites are unaffected.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test Humans.slnx -v quiet`
Expected: PASS — all green.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/DTOs/TicketTransferDtos.cs src/Humans.Web/ViewComponents/TicketHoldingsViewComponent.cs src/Humans.Web/Views/Shared/Components/TicketStub/Default.cshtml
git commit -m "feat(early-entry): show early-entry date on the holder's ticket stub"
```

---

## Task 10: Section docs

**Files:**
- Modify: `docs/sections/Shifts.md`
- Modify: `docs/sections/Camps.md`

- [ ] **Step 1: Note the EE contributor in each section doc**

In `docs/sections/Shifts.md`, under the cross-section dependencies / architecture status area, add a line:

```markdown
- **Early Entry contributor:** `VolunteerTrackingExportService` implements `IEarlyEntryProvider` — derives EE grants (first confirmed build-shift day − 1) for the cross-source EE roster. `ShiftSignupService` evicts the EE cache via `IEarlyEntryInvalidator` on build-shift confirm/bail.
```

In `docs/sections/Camps.md`, similarly:

```markdown
- **Early Entry contributor:** `CampService` implements `IEarlyEntryProvider` — emits one grant per Active `HasEarlyEntry` member (date = global `CampSettings.EeStartDate`). `SetEarlyEntryAsync` and the member-removal cascade evict the EE cache via `IEarlyEntryInvalidator`.
```

- [ ] **Step 2: Commit**

```bash
git add docs/sections/Shifts.md docs/sections/Camps.md
git commit -m "docs(early-entry): note EE contributor in Shifts + Camps section docs"
```

---

## Final verification

- [ ] **Full build + test:** `dotnet build Humans.slnx -v quiet` then `dotnet test Humans.slnx -v quiet` — all green.
- [ ] **Architecture tests:** the solution's architecture/analyzer tests pass (no cross-section repository access introduced; EE orchestrator owns no repository; providers read only their own section's repos).
- [ ] **Push:** `git push` (the branch already tracks `origin/early-entry-roster`).
- [ ] **Open the PR** to `peterdrier/Humans:main` once Peter approves (per the two-remote workflow; EE roster is a feature branch → PR to fork main).

---

## Spec coverage self-check

- Provider contract + `EarlyEntryGrant` with source label → Task 1, 2, 3.
- Camps provider (stored bool, global date, camp-name source) → Task 2, 6.
- Shifts provider (derived, build-only, first-shift − 1, team source, reuse export logic) → Task 3, 6.
- Orchestrator fan-out, earliest-date, source list, `HasMultiple` flag → Task 4.
- Caching decorator (`GetForUser` cached incl. negatives, roster uncached, no warmup) → Task 5, 7.
- §15e invalidator wired to Camps + Shifts write paths → Task 1, 5, 7.
- DI mirroring GDPR/Caching patterns → Task 7.
- Ticket-stub self-view (own ticket only, optional field) → Task 9.
- Roster at `/Shifts/Admin/EarlyEntry` with multi-source flag, shift-dashboard authz → Task 8.
- Section docs → Task 10.
