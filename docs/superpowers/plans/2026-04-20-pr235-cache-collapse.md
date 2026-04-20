# PR #235 Cache-Collapse Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rework PR #235 (`sprint/2026-04-16/issue-504`) so `CachingProfileService` owns a single `ConcurrentDictionary<Guid, FullProfile>` (no `IProfileStore`, no warmup hosted service, no `IMemoryCache` for profile data), folds `IVolunteerHistoryService` into `IProfileService` as a sub-aggregate, and exposes cross-section cache signalling via a new `IFullProfileInvalidator` interface.

**Architecture:** Application service stays pure (repository access only, no cache awareness); optional Infrastructure decorator owns a lazy-filled in-memory cache exposed via `ValueTask<FullProfile?> GetFullProfileAsync`. Cross-section invalidation goes through `IFullProfileInvalidator`. Removing the decorator leaves the app fully functional, just slower.

**Tech Stack:** .NET 10, C#, EF Core, Scrutor decoration, xUnit, NSubstitute, NodaTime.

**Spec:** `docs/superpowers/specs/2026-04-20-pr235-cache-collapse-design.md`

**Branch:** Amend commits on `sprint/2026-04-16/issue-504` (the existing PR #235 branch). Work in a worktree — never in the main checkout.

---

## File Structure

### New files

- `src/Humans.Application/FullProfile.cs` — record replacing `CachedProfile` (adds `LocalDate` on CV entries; top-level file, not nested in interface).
- `src/Humans.Application/CVEntry.cs` — record replacing `CachedVolunteerEntry`; `(LocalDate Date, string EventName, string? Description)`.
- `src/Humans.Application/Interfaces/IFullProfileInvalidator.cs` — one-method interface: `Task InvalidateAsync(Guid userId, CancellationToken ct)`.
- `src/Humans.Infrastructure/Services/Profiles/NullFullProfileInvalidator.cs` — no-op fallback for test contexts that wire the Application service standalone.
- `tests/Humans.Application.Tests/Services/CachingProfileServiceTests.cs` — decorator unit tests.

### Modified files

- `src/Humans.Application/Interfaces/IProfileService.cs` — drop `InvalidateCacheAsync`, add `GetFullProfileAsync` and `SaveCVEntriesAsync`.
- `src/Humans.Application/Services/Profile/ProfileService.cs` — implement `GetFullProfileAsync` (load+stitch) and `SaveCVEntriesAsync` (absorb reconcile logic from the deleted `VolunteerHistoryService`).
- `src/Humans.Infrastructure/Services/Profiles/CachingProfileService.cs` — own private `ConcurrentDictionary<Guid, FullProfile>`; implement `IProfileService` + `IFullProfileInvalidator`; drop store + IMemoryCache; write-intercept refresh.
- `src/Humans.Application/Interfaces/Repositories/IProfileRepository.cs` — add `ReconcileCVEntriesAsync(Guid profileId, IReadOnlyList<CVEntry> entries, CancellationToken ct)`.
- `src/Humans.Infrastructure/Repositories/ProfileRepository.cs` — implement `ReconcileCVEntriesAsync`.
- `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs` — DI changes (remove store / warmup / VolunteerHistory registrations; register `CachingProfileService` as both `IProfileService` and `IFullProfileInvalidator`).
- `src/Humans.Infrastructure/Services/AccountMergeService.cs` — replace `_profileService.InvalidateCacheAsync(…)` with `_fullProfileInvalidator.InvalidateAsync(…)`.
- `src/Humans.Infrastructure/Services/DuplicateAccountService.cs` — same.
- `src/Humans.Infrastructure/Jobs/SuspendNonCompliantMembersJob.cs` — same.
- `src/Humans.Infrastructure/Jobs/ProcessAccountDeletionsJob.cs` — same.
- `src/Humans.Web/ViewComponents/UserAvatarViewComponent.cs` — collapse `GetCachedProfile ?? GetCachedProfileAsync` into `await GetFullProfileAsync(...)` (methods already removed; this is currently broken or consuming the entity directly — align with new shape).
- `src/Humans.Web/TagHelpers/HumanLinkTagHelper.cs` — same pattern.
- `src/Humans.Web/ViewComponents/ProfileCardViewComponent.cs` — drop `IVolunteerHistoryService` dep; read `CVEntries` off `FullProfile`.
- `tests/Humans.Application.Tests/Architecture/ProfileArchitectureTests.cs` — drop store assertions, add `IFullProfileInvalidator` assertion.
- `tests/Humans.Application.Tests/Services/ProfileServiceTests.cs` — drop cache-method tests (already done by 88c97095), absorb `VolunteerHistoryServiceTests` reconcile coverage.
- `docs/architecture/design-rules.md` — rewrite §15 to the corrected shape.

### Deleted files

- `src/Humans.Application/Interfaces/Stores/IProfileStore.cs`
- `src/Humans.Infrastructure/Stores/ProfileStore.cs`
- `src/Humans.Infrastructure/HostedServices/ProfileStoreWarmupHostedService.cs`
- `src/Humans.Application/Interfaces/IVolunteerHistoryService.cs`
- `src/Humans.Application/Services/Profile/VolunteerHistoryService.cs`
- `src/Humans.Application/Interfaces/Repositories/IVolunteerHistoryRepository.cs`
- `src/Humans.Infrastructure/Repositories/VolunteerHistoryRepository.cs`
- `src/Humans.Infrastructure/Services/Profiles/CachingVolunteerHistoryService.cs`
- `tests/Humans.Application.Tests/Services/VolunteerHistoryServiceTests.cs`
- Any other standalone test file covering removed services (detect during deletion phase).

Profile's `Humans.Application/Interfaces/Stores/IProfileStore.cs` is removed, but the `Interfaces/Stores/` directory stays (Governance's `IApplicationStore.cs` still lives there; removing it is the #533 issue's scope).

---

## Phase 0 — Worktree + baseline

### Task 0.1: Create worktree and verify baseline build passes

**Files:** none yet

- [ ] **Step 1: Sync upstream refs**

```bash
git fetch origin sprint/2026-04-16/issue-504
git fetch upstream main
```

Expected: fetch completes without error.

- [ ] **Step 2: Create worktree off the PR branch**

```bash
git worktree add .worktrees/pr235-cache-collapse origin/sprint/2026-04-16/issue-504 -b sprint/2026-04-16/issue-504-cache-collapse
```

Working branch: `sprint/2026-04-16/issue-504-cache-collapse` (temporary; will force-push back to `sprint/2026-04-16/issue-504` at end — or merge into it with fast-forward after CI passes).

Actually simpler: just check out the existing branch in the worktree and commit onto it directly, pushing back with `--force-with-lease`. Revised command:

```bash
git worktree add .worktrees/pr235-cache-collapse
cd .worktrees/pr235-cache-collapse
git checkout sprint/2026-04-16/issue-504
git reset --hard origin/sprint/2026-04-16/issue-504
```

Expected: worktree created at `.worktrees/pr235-cache-collapse`, branch checked out at the PR head.

- [ ] **Step 3: Verify baseline build + tests pass**

From `.worktrees/pr235-cache-collapse/`:

```bash
dotnet build Humans.slnx
```

Expected: 0 errors, 0 warnings.

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~Humans.Application.Tests|FullyQualifiedName~Humans.Domain.Tests"
```

Expected: all tests pass (the PR description notes 1010/1011 — the known-failing DataProtection test may still fail; record the exact baseline number).

- [ ] **Step 4: Commit a worktree marker (no code change)**

Not required. Baseline noted; proceed.

---

## Phase 1 — Introduce FullProfile and CVEntry types (isolated, unused)

### Task 1.1: Add CVEntry record

**Files:**
- Create: `src/Humans.Application/CVEntry.cs`

- [ ] **Step 1: Write the record**

```csharp
using NodaTime;

namespace Humans.Application;

/// <summary>
/// Slim projection of a volunteer-history entry, as included in
/// <see cref="FullProfile"/>. Date is rendered as "MMM'yy" in the UI.
/// </summary>
public record CVEntry(LocalDate Date, string EventName, string? Description);
```

- [ ] **Step 2: Build**

```bash
dotnet build Humans.slnx
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/CVEntry.cs
git commit -m "Add CVEntry record (replaces CachedVolunteerEntry, adds Date)"
```

### Task 1.2: Add FullProfile record

**Files:**
- Create: `src/Humans.Application/FullProfile.cs`

- [ ] **Step 1: Write the record**

```csharp
using Humans.Domain.Entities;

namespace Humans.Application;

/// <summary>
/// Denormalized profile projection used by the caching decorator and its
/// consumers (avatar, link tag helper, profile card). Stitched from
/// <see cref="Profile"/> + owning <see cref="User"/> + CV entries.
/// </summary>
public record FullProfile(
    Guid UserId, string DisplayName, string? ProfilePictureUrl,
    bool HasCustomPicture, Guid ProfileId, long UpdatedAtTicks,
    string? BurnerName, string? Bio, string? Pronouns,
    string? ContributionInterests,
    string? City, string? CountryCode, double? Latitude, double? Longitude,
    int? BirthdayDay, int? BirthdayMonth,
    bool IsApproved, bool IsSuspended,
    IReadOnlyList<CVEntry> CVEntries,
    string? NotificationEmail = null)
{
    public static FullProfile Create(
        Profile profile,
        User user,
        IReadOnlyList<VolunteerHistoryEntry> volunteerHistory,
        string? notificationEmail = null) => new(
            UserId: user.Id,
            DisplayName: user.DisplayName,
            ProfilePictureUrl: user.ProfilePictureUrl,
            HasCustomPicture: profile.ProfilePictureData is not null,
            ProfileId: profile.Id,
            UpdatedAtTicks: profile.UpdatedAt.ToUnixTimeTicks(),
            BurnerName: profile.BurnerName,
            Bio: profile.Bio,
            Pronouns: profile.Pronouns,
            ContributionInterests: profile.ContributionInterests,
            City: profile.City,
            CountryCode: profile.CountryCode,
            Latitude: profile.Latitude,
            Longitude: profile.Longitude,
            BirthdayDay: profile.DateOfBirth?.Day,
            BirthdayMonth: profile.DateOfBirth?.Month,
            IsApproved: profile.IsApproved,
            IsSuspended: profile.IsSuspended,
            CVEntries: volunteerHistory
                .OrderByDescending(v => v.Date)
                .Select(v => new CVEntry(v.Date, v.EventName, v.Description))
                .ToList(),
            NotificationEmail: notificationEmail);
}
```

Notes on fields: mirror the existing `CachedProfile` exactly so downstream consumers keep working. Verify each field against `IProfileStore.cs` on the branch — adjust if any field names differ (e.g., `BirthdayDay`/`BirthdayMonth` may already split differently).

- [ ] **Step 2: Build**

```bash
dotnet build Humans.slnx
```

Expected: 0 errors, 0 warnings. (The type is unused at this point — compiler treats it as reachable-but-unreferenced, which is fine.)

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/FullProfile.cs
git commit -m "Add FullProfile record (replaces CachedProfile)"
```

---

## Phase 2 — Base GetFullProfileAsync on IProfileService

### Task 2.1: Add IProfileService.GetFullProfileAsync signature

**Files:**
- Modify: `src/Humans.Application/Interfaces/IProfileService.cs`

- [ ] **Step 1: Add method to interface**

In `IProfileService`, after `GetProfileAsync`:

```csharp
/// <summary>
/// Returns the denormalized <see cref="FullProfile"/> projection for the
/// given user, stitched from Profile + User + CV entries. The caching
/// decorator serves dict hits synchronously; the base implementation loads
/// from repositories each call.
/// </summary>
ValueTask<FullProfile?> GetFullProfileAsync(Guid userId, CancellationToken ct = default);
```

- [ ] **Step 2: Build — expect failure**

```bash
dotnet build Humans.slnx
```

Expected: build FAILS (ProfileService and CachingProfileService don't implement the new method yet).

### Task 2.2: Write failing test for ProfileService.GetFullProfileAsync

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/ProfileServiceTests.cs`

- [ ] **Step 1: Add test fixture methods and a new test**

Append a new `[Fact]` test to the existing `ProfileServiceTests` class. Follow the existing `_service = new ProfileService(...)` setup pattern.

```csharp
[Fact]
public async Task GetFullProfileAsync_ReturnsStitchedProjection_WhenProfileExists()
{
    var userId = Guid.NewGuid();
    var profileId = Guid.NewGuid();

    var profile = new Profile
    {
        Id = profileId,
        UserId = userId,
        BurnerName = "Burner",
        Bio = "Bio text",
        City = "Madrid",
        IsApproved = true,
    };

    var user = new User { Id = userId, DisplayName = "Real Name", ProfilePictureUrl = "https://img" };

    _profileRepository.GetByUserIdAsync(userId, Arg.Any<CancellationToken>())
        .Returns(profile);
    _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>())
        .Returns(user);

    var result = await _service.GetFullProfileAsync(userId);

    result.Should().NotBeNull();
    result!.UserId.Should().Be(userId);
    result.DisplayName.Should().Be("Real Name");
    result.ProfilePictureUrl.Should().Be("https://img");
    result.ProfileId.Should().Be(profileId);
    result.BurnerName.Should().Be("Burner");
    result.City.Should().Be("Madrid");
    result.IsApproved.Should().BeTrue();
    result.CVEntries.Should().BeEmpty();
}

[Fact]
public async Task GetFullProfileAsync_ReturnsNull_WhenProfileMissing()
{
    var userId = Guid.NewGuid();
    _profileRepository.GetByUserIdAsync(userId, Arg.Any<CancellationToken>())
        .Returns((Profile?)null);

    var result = await _service.GetFullProfileAsync(userId);

    result.Should().BeNull();
}
```

If `_profileRepository` or `_userService` substitutes don't already exist on the fixture, add them (mirror the existing pattern — the file already injects these for other tests).

- [ ] **Step 2: Run test — expect fail**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~ProfileServiceTests.GetFullProfileAsync"
```

Expected: FAIL — either compilation error (method missing) or `NotImplementedException`.

### Task 2.3: Implement ProfileService.GetFullProfileAsync

**Files:**
- Modify: `src/Humans.Application/Services/Profile/ProfileService.cs`

- [ ] **Step 1: Add implementation**

In `ProfileService`, add:

```csharp
public async ValueTask<FullProfile?> GetFullProfileAsync(Guid userId, CancellationToken ct = default)
{
    var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
    if (profile is null) return null;

    var user = await _userService.GetByIdAsync(userId, ct);
    if (user is null) return null;

    return FullProfile.Create(profile, user, profile.VolunteerHistory.ToList());
}
```

`GetByUserIdAsync` already `Include`s `VolunteerHistory` per commit `83767529`. Confirm by reading `ProfileRepository.GetByUserIdAsync` on the branch; if not, add the include there.

- [ ] **Step 2: Run test — expect pass**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~ProfileServiceTests.GetFullProfileAsync"
```

Expected: both GetFullProfileAsync tests PASS.

- [ ] **Step 3: Build to confirm no regressions**

```bash
dotnet build Humans.slnx
```

Expected: FAILS — `CachingProfileService` still missing the implementation. That's expected; Phase 3 fixes it.

### Task 2.4: Provide pass-through implementation on CachingProfileService (temporary)

**Files:**
- Modify: `src/Humans.Infrastructure/Services/Profiles/CachingProfileService.cs`

- [ ] **Step 1: Add a naive pass-through**

```csharp
public ValueTask<FullProfile?> GetFullProfileAsync(Guid userId, CancellationToken ct = default) =>
    _inner.GetFullProfileAsync(userId, ct);
```

This is temporary — the dict-cached implementation lands in Phase 3. Needed now so the project builds and other phases can proceed.

- [ ] **Step 2: Build + full test**

```bash
dotnet build Humans.slnx
dotnet test Humans.slnx
```

Expected: build clean, all tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Interfaces/IProfileService.cs \
        src/Humans.Application/Services/Profile/ProfileService.cs \
        src/Humans.Infrastructure/Services/Profiles/CachingProfileService.cs \
        tests/Humans.Application.Tests/Services/ProfileServiceTests.cs
git commit -m "Add GetFullProfileAsync to IProfileService with base impl"
```

---

## Phase 3 — Decorator-owned ConcurrentDictionary

### Task 3.1: Write failing test for dict hit / miss behavior

**Files:**
- Create: `tests/Humans.Application.Tests/Services/CachingProfileServiceTests.cs`

- [ ] **Step 1: Write test class skeleton + two tests**

```csharp
using Humans.Application;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Services.Profiles;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services;

public class CachingProfileServiceTests
{
    private readonly IProfileService _inner = Substitute.For<IProfileService>();
    private readonly IProfileRepository _profileRepository = Substitute.For<IProfileRepository>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailRepository _userEmailRepository = Substitute.For<IUserEmailRepository>();
    private readonly INavBadgeCacheInvalidator _navBadge = Substitute.For<INavBadgeCacheInvalidator>();
    private readonly INotificationMeterCacheInvalidator _notificationMeter = Substitute.For<INotificationMeterCacheInvalidator>();

    private CachingProfileService CreateSut() => new(
        _inner, _profileRepository, _userService, _userEmailRepository, _navBadge, _notificationMeter);

    [Fact]
    public async Task GetFullProfileAsync_DictMiss_DelegatesToInnerAndPopulatesDict()
    {
        var userId = Guid.NewGuid();
        var fullProfile = SampleFullProfile(userId);
        _inner.GetFullProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<FullProfile?>(fullProfile));

        var sut = CreateSut();

        var first = await sut.GetFullProfileAsync(userId);
        first.Should().BeSameAs(fullProfile);
        await _inner.Received(1).GetFullProfileAsync(userId, Arg.Any<CancellationToken>());

        var second = await sut.GetFullProfileAsync(userId);
        second.Should().BeSameAs(fullProfile);
        await _inner.Received(1).GetFullProfileAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFullProfileAsync_DictMissReturnsNull_DoesNotPopulateDict()
    {
        var userId = Guid.NewGuid();
        _inner.GetFullProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<FullProfile?>((FullProfile?)null));

        var sut = CreateSut();

        var first = await sut.GetFullProfileAsync(userId);
        first.Should().BeNull();

        var second = await sut.GetFullProfileAsync(userId);
        await _inner.Received(2).GetFullProfileAsync(userId, Arg.Any<CancellationToken>());
    }

    private static FullProfile SampleFullProfile(Guid userId) => new(
        UserId: userId, DisplayName: "Name", ProfilePictureUrl: null,
        HasCustomPicture: false, ProfileId: Guid.NewGuid(), UpdatedAtTicks: 0,
        BurnerName: null, Bio: null, Pronouns: null, ContributionInterests: null,
        City: null, CountryCode: null, Latitude: null, Longitude: null,
        BirthdayDay: null, BirthdayMonth: null,
        IsApproved: true, IsSuspended: false,
        CVEntries: Array.Empty<CVEntry>(),
        NotificationEmail: null);
}
```

Note: constructor signature `new(IProfileService, IProfileRepository, IUserService, IUserEmailRepository, INavBadge…, INotificationMeter…)` — adjust to match what Phase 3.3 produces. Keep it in sync as you iterate.

- [ ] **Step 2: Run — expect fail**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~CachingProfileServiceTests"
```

Expected: tests fail — either compilation (constructor mismatch) or dict-population assertion (since current decorator has no dict).

### Task 3.2: Reshape CachingProfileService (drop IProfileStore + IMemoryCache in one shot)

**Files:**
- Modify: `src/Humans.Infrastructure/Services/Profiles/CachingProfileService.cs`

- [ ] **Step 1: Replace fields and constructor with the final shape**

Target state — store and IMemoryCache gone entirely:

```csharp
using System.Collections.Concurrent;
using Humans.Application;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;

// remove: using Microsoft.Extensions.Caching.Memory;
// remove: using Humans.Application.Interfaces.Stores;
// remove: using Humans.Infrastructure.Caching;

namespace Humans.Infrastructure.Services.Profiles;

public sealed class CachingProfileService : IProfileService
{
    private readonly IProfileService _inner;
    private readonly IProfileRepository _profileRepository;
    private readonly IUserService _userService;
    private readonly IUserEmailRepository _userEmailRepository;
    private readonly INavBadgeCacheInvalidator _navBadge;
    private readonly INotificationMeterCacheInvalidator _notificationMeter;

    private readonly ConcurrentDictionary<Guid, FullProfile> _byUserId = new();

    public CachingProfileService(
        IProfileService inner,
        IProfileRepository profileRepository,
        IUserService userService,
        IUserEmailRepository userEmailRepository,
        INavBadgeCacheInvalidator navBadge,
        INotificationMeterCacheInvalidator notificationMeter)
    {
        _inner = inner;
        _profileRepository = profileRepository;
        _userService = userService;
        _userEmailRepository = userEmailRepository;
        _navBadge = navBadge;
        _notificationMeter = notificationMeter;
    }
```

`IFullProfileInvalidator` doesn't exist yet — added in Phase 6 (class signature updated there).

- [ ] **Step 2: Replace `GetProfileAsync` (the IMemoryCache-wrapped one) with a pure pass-through**

```csharp
public Task<Profile?> GetProfileAsync(Guid userId, CancellationToken ct = default) =>
    _inner.GetProfileAsync(userId, ct);
```

- [ ] **Step 3: Implement dict-cached GetFullProfileAsync**

Replace the Phase 2.4 temporary pass-through:

```csharp
public ValueTask<FullProfile?> GetFullProfileAsync(Guid userId, CancellationToken ct = default)
{
    if (_byUserId.TryGetValue(userId, out var hit))
        return new ValueTask<FullProfile?>(hit);

    return new ValueTask<FullProfile?>(LoadAndCacheAsync(userId, ct));
}

private async Task<FullProfile?> LoadAndCacheAsync(Guid userId, CancellationToken ct)
{
    var result = await _inner.GetFullProfileAsync(userId, ct);
    if (result is not null)
        _byUserId[userId] = result;
    return result;
}
```

- [ ] **Step 4: Leave DI registrations for IProfileStore + warmup hosted service untouched for now**

Scrutor `Decorate<IProfileService, CachingProfileService>()` resolves constructor deps automatically; since the decorator no longer takes `IProfileStore` or `IMemoryCache`, it simply doesn't resolve them. The `AddSingleton<IProfileStore, ProfileStore>()` + `AddHostedService<ProfileStoreWarmupHostedService>()` lines remain in `InfrastructureServiceCollectionExtensions.cs` as dead registrations until Phase 9 deletes the types.

- [ ] **Step 5: Build**

```bash
dotnet build Humans.slnx
```

Expected: clean. If anything else in the codebase references `IProfileStore` (outside the dead-but-registered code and Governance's separate `IApplicationStore`), flag and fix — then continue.

- [ ] **Step 6: Run decorator tests**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~CachingProfileServiceTests"
```

Expected: both dict-hit / dict-miss tests from Task 3.1 now PASS.

- [ ] **Step 7: Run full suite**

```bash
dotnet test Humans.slnx
```

Expected: all pass. (If `UserAvatarViewComponent` or `HumanLinkTagHelper` tests fail because they previously called `GetCachedProfile*` — already removed by 88c97095 — consumer updates happen in Phase 10; record the failure and continue.)

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Infrastructure/Services/Profiles/CachingProfileService.cs \
        tests/Humans.Application.Tests/Services/CachingProfileServiceTests.cs
git commit -m "Decorator owns FullProfile dict; drop IProfileStore + IMemoryCache"
```

---

## Phase 4 — Fold SaveCVEntries into ProfileService

### Task 4.1: Add ReconcileCVEntriesAsync to IProfileRepository

**Files:**
- Modify: `src/Humans.Application/Interfaces/Repositories/IProfileRepository.cs`

- [ ] **Step 1: Add method to interface**

```csharp
/// <summary>
/// Reconciles the CV-entry collection for the given profile with the
/// provided set. Adds, updates, and removes entries as needed, then saves.
/// </summary>
Task ReconcileCVEntriesAsync(
    Guid profileId,
    IReadOnlyList<CVEntry> entries,
    CancellationToken ct = default);
```

### Task 4.2: Write failing test for ProfileRepository.ReconcileCVEntriesAsync

**Files:**
- Modify or Create: `tests/Humans.Infrastructure.Tests/Repositories/ProfileRepositoryTests.cs`

If an integration test fixture with a real DbContext doesn't exist in `tests/Humans.Infrastructure.Tests/`, use the existing in-memory-DB pattern (look in `tests/Humans.Application.Tests/` for `HumansDbContext.InMemory(...)` helpers).

- [ ] **Step 1: Add a test that covers add / update / remove**

```csharp
[Fact]
public async Task ReconcileCVEntriesAsync_AddsUpdatesAndRemovesEntries()
{
    using var ctx = HumansDbContextHelper.CreateInMemory();
    var profile = new Profile { Id = Guid.NewGuid(), UserId = Guid.NewGuid() };
    profile.VolunteerHistory.Add(new VolunteerHistoryEntry
    {
        Id = Guid.NewGuid(), ProfileId = profile.Id,
        Date = new LocalDate(2024, 3, 1), EventName = "Keep me", Description = "Old desc"
    });
    profile.VolunteerHistory.Add(new VolunteerHistoryEntry
    {
        Id = Guid.NewGuid(), ProfileId = profile.Id,
        Date = new LocalDate(2024, 4, 1), EventName = "Remove me", Description = null
    });
    ctx.Profiles.Add(profile);
    await ctx.SaveChangesAsync();

    var repo = new ProfileRepository(ctx, /* other deps */);

    var newEntries = new List<CVEntry>
    {
        new(new LocalDate(2024, 3, 1), "Keep me", "New desc"),
        new(new LocalDate(2024, 5, 1), "Add me", null),
    };

    await repo.ReconcileCVEntriesAsync(profile.Id, newEntries, default);

    var persisted = await ctx.VolunteerHistoryEntries
        .Where(v => v.ProfileId == profile.Id)
        .OrderBy(v => v.Date)
        .ToListAsync();

    persisted.Should().HaveCount(2);
    persisted[0].EventName.Should().Be("Keep me");
    persisted[0].Description.Should().Be("New desc");
    persisted[1].EventName.Should().Be("Add me");
}
```

- [ ] **Step 2: Run — expect fail**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~ProfileRepositoryTests.ReconcileCVEntries"
```

Expected: method does not exist.

### Task 4.3: Implement ProfileRepository.ReconcileCVEntriesAsync

**Files:**
- Modify: `src/Humans.Infrastructure/Repositories/ProfileRepository.cs`

- [ ] **Step 1: Port reconcile logic from VolunteerHistoryService.ReconcileEntriesAsync**

Read the current `src/Humans.Application/Services/Profile/VolunteerHistoryService.cs` on the branch. Its core logic (diff existing vs. incoming, add new, remove missing, update matched) moves here verbatim, with DbContext already available in the repo:

```csharp
public async Task ReconcileCVEntriesAsync(
    Guid profileId,
    IReadOnlyList<CVEntry> entries,
    CancellationToken ct = default)
{
    var existing = await _dbContext.VolunteerHistoryEntries
        .Where(v => v.ProfileId == profileId)
        .ToListAsync(ct);

    // Match by (Date + EventName) — same keying currently used by VolunteerHistoryService.
    var incoming = entries.ToLookup(e => (e.Date, e.EventName));
    var existingLookup = existing.ToLookup(v => (v.Date, v.EventName));

    // Remove entries not present in incoming
    var toRemove = existing.Where(v => !incoming.Contains((v.Date, v.EventName))).ToList();
    _dbContext.VolunteerHistoryEntries.RemoveRange(toRemove);

    // Update descriptions on matched entries; add new ones
    foreach (var entry in entries)
    {
        var match = existingLookup[(entry.Date, entry.EventName)].FirstOrDefault();
        if (match is not null)
        {
            match.Description = entry.Description;
            match.UpdatedAt = _clock.GetCurrentInstant();
        }
        else
        {
            _dbContext.VolunteerHistoryEntries.Add(new VolunteerHistoryEntry
            {
                Id = Guid.NewGuid(),
                ProfileId = profileId,
                Date = entry.Date,
                EventName = entry.EventName,
                Description = entry.Description,
                CreatedAt = _clock.GetCurrentInstant(),
                UpdatedAt = _clock.GetCurrentInstant(),
            });
        }
    }

    await _dbContext.SaveChangesAsync(ct);
}
```

Exact keying and clock handling: mirror whatever `VolunteerHistoryService.ReconcileEntriesAsync` currently does on the branch. If `ProfileRepository` doesn't already inject `IClock`, add it (follow the existing repo pattern).

- [ ] **Step 2: Run repo test — expect pass**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~ProfileRepositoryTests.ReconcileCVEntries"
```

Expected: PASS.

### Task 4.4: Write failing test for IProfileService.SaveCVEntriesAsync

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/ProfileServiceTests.cs`

- [ ] **Step 1: Add test**

```csharp
[Fact]
public async Task SaveCVEntriesAsync_DelegatesToRepository()
{
    var userId = Guid.NewGuid();
    var profileId = Guid.NewGuid();
    var profile = new Profile { Id = profileId, UserId = userId };
    _profileRepository.GetByUserIdAsync(userId, Arg.Any<CancellationToken>())
        .Returns(profile);

    var entries = new List<CVEntry>
    {
        new(new LocalDate(2025, 3, 1), "Nowhere 2025", "Sound crew"),
    };

    await _service.SaveCVEntriesAsync(userId, entries);

    await _profileRepository.Received(1)
        .ReconcileCVEntriesAsync(profileId, entries, Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Run — expect fail**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~ProfileServiceTests.SaveCVEntries"
```

Expected: method does not exist.

### Task 4.5: Implement IProfileService.SaveCVEntriesAsync

**Files:**
- Modify: `src/Humans.Application/Interfaces/IProfileService.cs`
- Modify: `src/Humans.Application/Services/Profile/ProfileService.cs`

- [ ] **Step 1: Add to interface**

```csharp
Task SaveCVEntriesAsync(Guid userId, IReadOnlyList<CVEntry> entries, CancellationToken ct = default);
```

- [ ] **Step 2: Implement in ProfileService**

```csharp
public async Task SaveCVEntriesAsync(Guid userId, IReadOnlyList<CVEntry> entries, CancellationToken ct = default)
{
    var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
    if (profile is null) return;

    await _profileRepository.ReconcileCVEntriesAsync(profile.Id, entries, ct);
}
```

- [ ] **Step 3: Pass-through on decorator (write-through lands in Phase 5)**

In `CachingProfileService`:

```csharp
public Task SaveCVEntriesAsync(Guid userId, IReadOnlyList<CVEntry> entries, CancellationToken ct = default) =>
    _inner.SaveCVEntriesAsync(userId, entries, ct);
```

- [ ] **Step 4: Run + commit**

```bash
dotnet build Humans.slnx
dotnet test Humans.slnx --filter "FullyQualifiedName~ProfileServiceTests.SaveCVEntries|FullyQualifiedName~ProfileRepositoryTests.ReconcileCVEntries"
git add -A
git commit -m "Add IProfileService.SaveCVEntriesAsync and repo reconcile"
```

Expected: tests pass.

---

## Phase 5 — Decorator write-through (refresh dict after writes)

### Task 5.1: Write failing test for SaveCVEntries refreshing the dict

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/CachingProfileServiceTests.cs`

- [ ] **Step 1: Add test**

```csharp
[Fact]
public async Task SaveCVEntriesAsync_RefreshesDictEntry()
{
    var userId = Guid.NewGuid();
    var profile = new Profile { Id = Guid.NewGuid(), UserId = userId };
    var user = new User { Id = userId, DisplayName = "U" };

    _profileRepository.GetByUserIdAsync(userId, Arg.Any<CancellationToken>())
        .Returns(profile);
    _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>())
        .Returns(user);

    var sut = CreateSut();

    // Pre-warm dict with a stale entry
    var stale = SampleFullProfile(userId) with { CVEntries = Array.Empty<CVEntry>() };
    _inner.GetFullProfileAsync(userId, Arg.Any<CancellationToken>())
        .Returns(new ValueTask<FullProfile?>(stale));
    await sut.GetFullProfileAsync(userId);

    // Mutate history in the entity that the refresh will re-read
    profile.VolunteerHistory.Add(new VolunteerHistoryEntry
    {
        Id = Guid.NewGuid(), ProfileId = profile.Id,
        Date = new LocalDate(2025, 3, 1), EventName = "Nowhere 2025", Description = null,
    });

    await sut.SaveCVEntriesAsync(userId, new[] { new CVEntry(new LocalDate(2025, 3, 1), "Nowhere 2025", null) });

    // Assert: decorator refresh re-read through _profileRepository / _userService and upserted
    await _profileRepository.Received(1).GetByUserIdAsync(userId, Arg.Any<CancellationToken>());
    var fresh = await sut.GetFullProfileAsync(userId);
    fresh!.CVEntries.Should().ContainSingle(e => e.EventName == "Nowhere 2025");
}
```

- [ ] **Step 2: Run — expect fail**

Expected: fresh entry still has empty CVEntries because the decorator doesn't refresh after writes yet.

### Task 5.2: Implement RefreshEntryAsync on CachingProfileService

**Files:**
- Modify: `src/Humans.Infrastructure/Services/Profiles/CachingProfileService.cs`

- [ ] **Step 1: Add helper**

```csharp
private async Task RefreshEntryAsync(Guid userId, CancellationToken ct)
{
    var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
    if (profile is null)
    {
        _byUserId.TryRemove(userId, out _);
        return;
    }

    var user = await _userService.GetByIdAsync(userId, ct);
    if (user is null)
    {
        _byUserId.TryRemove(userId, out _);
        return;
    }

    var notificationEmail = await _userEmailRepository.GetNotificationEmailAsync(userId, ct);

    _byUserId[userId] = FullProfile.Create(
        profile,
        user,
        profile.VolunteerHistory.ToList(),
        notificationEmail);
}
```

Method name `GetNotificationEmailAsync` — verify against the actual `IUserEmailRepository` on the branch; adjust to the real method name.

- [ ] **Step 2: Hook into SaveCVEntries + other write paths**

Replace the Phase 4 pass-through:

```csharp
public async Task SaveCVEntriesAsync(Guid userId, IReadOnlyList<CVEntry> entries, CancellationToken ct = default)
{
    await _inner.SaveCVEntriesAsync(userId, entries, ct);
    await RefreshEntryAsync(userId, ct);
}
```

Also instrument the existing write paths in `CachingProfileService` (`SaveProfileAsync`, `SetMembershipTierAsync`, `RequestDeletionAsync`, `CancelDeletionAsync`, `SaveProfileLanguagesAsync`, and any other mutation method currently on the decorator) — each one: delegate, then `await RefreshEntryAsync(userId, ct)`. Existing per-user IMemoryCache invalidation and nav-badge/notification-meter invalidation stay.

- [ ] **Step 3: Run write-refresh test**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~CachingProfileServiceTests.SaveCVEntries"
```

Expected: PASS.

- [ ] **Step 4: Run full suite**

```bash
dotnet test Humans.slnx
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Services/Profiles/CachingProfileService.cs \
        tests/Humans.Application.Tests/Services/CachingProfileServiceTests.cs
git commit -m "Decorator refreshes dict entry after Profile mutations"
```

---

## Phase 6 — IFullProfileInvalidator interface

### Task 6.1: Create the interface

**Files:**
- Create: `src/Humans.Application/Interfaces/IFullProfileInvalidator.cs`

- [ ] **Step 1: Write interface**

```csharp
namespace Humans.Application.Interfaces;

/// <summary>
/// One-way cache-staleness signal for <see cref="FullProfile"/>. Implemented by
/// the caching decorator in Infrastructure; external sections inject this when
/// they change user state in ways that affect Profile's cached view.
/// Invalidation reloads the entry from repositories (preserving the fully-warm
/// invariant) rather than evicting — removing an entry only if the user no
/// longer has a profile.
/// </summary>
public interface IFullProfileInvalidator
{
    Task InvalidateAsync(Guid userId, CancellationToken ct = default);
}
```

- [ ] **Step 2: Build**

```bash
dotnet build Humans.slnx
```

Expected: clean.

### Task 6.2: Implement on CachingProfileService

**Files:**
- Modify: `src/Humans.Infrastructure/Services/Profiles/CachingProfileService.cs`

- [ ] **Step 1: Add interface to class declaration**

```csharp
public sealed class CachingProfileService : IProfileService, IFullProfileInvalidator
```

- [ ] **Step 2: Add InvalidateAsync**

```csharp
public Task InvalidateAsync(Guid userId, CancellationToken ct = default) =>
    RefreshEntryAsync(userId, ct);
```

Same semantics as write-refresh: reload-or-remove.

### Task 6.3: Write failing test for invalidator behavior

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/CachingProfileServiceTests.cs`

- [ ] **Step 1: Add two tests**

```csharp
[Fact]
public async Task InvalidateAsync_ExistingUser_ReloadsEntry()
{
    var userId = Guid.NewGuid();
    var profile = new Profile { Id = Guid.NewGuid(), UserId = userId, BurnerName = "Old" };
    var user = new User { Id = userId, DisplayName = "U" };

    _profileRepository.GetByUserIdAsync(userId, Arg.Any<CancellationToken>())
        .Returns(profile);
    _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>())
        .Returns(user);

    var sut = CreateSut();
    await sut.GetFullProfileAsync(userId);    // seed dict

    profile.BurnerName = "New";
    await ((IFullProfileInvalidator)sut).InvalidateAsync(userId);

    var fresh = await sut.GetFullProfileAsync(userId);
    fresh!.BurnerName.Should().Be("New");
}

[Fact]
public async Task InvalidateAsync_DeletedUser_RemovesEntry()
{
    var userId = Guid.NewGuid();
    var profile = new Profile { Id = Guid.NewGuid(), UserId = userId };
    var user = new User { Id = userId, DisplayName = "U" };

    _profileRepository.GetByUserIdAsync(userId, Arg.Any<CancellationToken>())
        .Returns(profile, (Profile?)null);
    _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>())
        .Returns(user);

    var sut = CreateSut();
    await sut.GetFullProfileAsync(userId);    // seed dict

    await ((IFullProfileInvalidator)sut).InvalidateAsync(userId);

    _inner.GetFullProfileAsync(userId, Arg.Any<CancellationToken>())
        .Returns(new ValueTask<FullProfile?>((FullProfile?)null));

    var second = await sut.GetFullProfileAsync(userId);
    second.Should().BeNull();
}
```

- [ ] **Step 2: Run — expect pass**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~CachingProfileServiceTests.Invalidate"
```

Expected: PASS.

### Task 6.4: Add NullFullProfileInvalidator

**Files:**
- Create: `src/Humans.Infrastructure/Services/Profiles/NullFullProfileInvalidator.cs`

- [ ] **Step 1: Write no-op implementation**

```csharp
using Humans.Application.Interfaces;

namespace Humans.Infrastructure.Services.Profiles;

/// <summary>
/// No-op fallback used in test contexts that wire up the base ProfileService
/// without the caching decorator.
/// </summary>
public sealed class NullFullProfileInvalidator : IFullProfileInvalidator
{
    public Task InvalidateAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;
}
```

### Task 6.5: Register CachingProfileService as both interfaces (single instance)

**Files:**
- Modify: `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs`

- [ ] **Step 1: Rewire DI**

Locate the section that registers `IProfileService` and `Decorate<IProfileService, CachingProfileService>()`. Change to a pattern that exposes the same decorator instance via both interfaces:

```csharp
// Register the base Application service
services.AddScoped<IProfileService, ProfileService>();

// Decorate and make the decorator also serve as IFullProfileInvalidator
services.Decorate<IProfileService, CachingProfileService>();
services.AddScoped<IFullProfileInvalidator>(sp =>
    (IFullProfileInvalidator)sp.GetRequiredService<IProfileService>());
```

Note: this relies on `IProfileService`'s registered implementation being the `CachingProfileService` *after* decoration, which is how Scrutor works. Verify at runtime (add a DI-registration integration test if the project already has one).

- [ ] **Step 2: Add a DI integration test to verify same-instance invariant**

Create `tests/Humans.Web.Tests/DependencyInjection/ProfileServiceRegistrationTests.cs` (or extend an existing DI test). Build a `ServiceCollection` with the real `InfrastructureServiceCollectionExtensions.AddHumansInfrastructure(...)` call, build the provider, resolve both `IProfileService` and `IFullProfileInvalidator`, and assert they are the same object reference:

```csharp
[Fact]
public void IProfileService_And_IFullProfileInvalidator_ResolveToSameInstance()
{
    var services = new ServiceCollection();
    // ...minimum deps for Humans.Infrastructure registration (mirror existing DI tests)...
    services.AddHumansInfrastructure(BuildConfiguration());

    using var sp = services.BuildServiceProvider();
    using var scope = sp.CreateScope();

    var profileService = scope.ServiceProvider.GetRequiredService<IProfileService>();
    var invalidator = scope.ServiceProvider.GetRequiredService<IFullProfileInvalidator>();

    invalidator.Should().BeSameAs(profileService);
}
```

If the existing DI test suite doesn't have this kind of end-to-end registration harness, skip — and instead add a comment in `InfrastructureServiceCollectionExtensions.cs` explaining why the `sp.GetRequiredService<IProfileService>()` cast is required to preserve single-instance state.

- [ ] **Step 3: Build + test**

```bash
dotnet build Humans.slnx
dotnet test Humans.slnx
```

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Interfaces/IFullProfileInvalidator.cs \
        src/Humans.Infrastructure/Services/Profiles/CachingProfileService.cs \
        src/Humans.Infrastructure/Services/Profiles/NullFullProfileInvalidator.cs \
        src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs \
        tests/Humans.Application.Tests/Services/CachingProfileServiceTests.cs
git commit -m "Introduce IFullProfileInvalidator, implement on CachingProfileService"
```

---

## Phase 7 — Migrate external callers from InvalidateCacheAsync to IFullProfileInvalidator

### Task 7.1: AccountMergeService

**Files:**
- Modify: `src/Humans.Infrastructure/Services/AccountMergeService.cs`

- [ ] **Step 1: Inject IFullProfileInvalidator**

Add constructor parameter `IFullProfileInvalidator fullProfileInvalidator` and field `_fullProfileInvalidator`.

- [ ] **Step 2: Replace call site**

Find `_profileService.InvalidateCacheAsync(sourceUser.Id, ...)` (around line 155 on main; branch may have shifted). Replace with:

```csharp
await _fullProfileInvalidator.InvalidateAsync(sourceUser.Id, ct);
```

- [ ] **Step 3: Update tests**

In `tests/Humans.Application.Tests/.../AccountMergeServiceTests.cs` (path TBD — locate via grep), inject an `IFullProfileInvalidator` substitute and assert `.Received(1).InvalidateAsync(...)` in place of the previous assertion on `_profileService.InvalidateCacheAsync`.

- [ ] **Step 4: Run + commit**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~AccountMergeServiceTests"
git add src/Humans.Infrastructure/Services/AccountMergeService.cs \
        tests/Humans.Application.Tests/.../AccountMergeServiceTests.cs
git commit -m "AccountMergeService: use IFullProfileInvalidator for cache signal"
```

### Task 7.2: DuplicateAccountService

**Files:**
- Modify: `src/Humans.Infrastructure/Services/DuplicateAccountService.cs`

Same shape as 7.1. Steps identical, substituting `DuplicateAccountService`.

- [ ] **Step 1: Inject IFullProfileInvalidator**
- [ ] **Step 2: Replace call site**
- [ ] **Step 3: Update tests**
- [ ] **Step 4: Run + commit**

### Task 7.3: SuspendNonCompliantMembersJob

**Files:**
- Modify: `src/Humans.Infrastructure/Jobs/SuspendNonCompliantMembersJob.cs`

Same shape. The current call currently passes a non-null `CachedProfile`-like value to `UpdateProfileCache`; with invalidator semantics, the decorator reloads the entry fresh, so just call `InvalidateAsync(userId, ct)` — don't stitch a projection by hand.

- [ ] **Step 1-4: as above**

### Task 7.4: ProcessAccountDeletionsJob

**Files:**
- Modify: `src/Humans.Infrastructure/Jobs/ProcessAccountDeletionsJob.cs`

Same shape. After deletion, `InvalidateAsync` finds no profile and removes the dict entry.

- [ ] **Step 1-4: as above**

---

## Phase 8 — Remove InvalidateCacheAsync from IProfileService

### Task 8.1: Remove method + decorator implementation

**Files:**
- Modify: `src/Humans.Application/Interfaces/IProfileService.cs`
- Modify: `src/Humans.Application/Services/Profile/ProfileService.cs`
- Modify: `src/Humans.Infrastructure/Services/Profiles/CachingProfileService.cs`

- [ ] **Step 1: Delete from interface**

Remove the `Task InvalidateCacheAsync(Guid userId, CancellationToken ct = default);` declaration.

- [ ] **Step 2: Delete the no-op base implementation (if any) and the decorator's implementation (if distinct from `InvalidateAsync`)**

After Phase 7, no caller references `_profileService.InvalidateCacheAsync` — confirm via:

```bash
grep -rn "InvalidateCacheAsync" src/ tests/
```

Expected: no matches.

- [ ] **Step 3: Build + test + commit**

```bash
dotnet build Humans.slnx
dotnet test Humans.slnx
git add -A
git commit -m "Remove InvalidateCacheAsync from IProfileService (callers migrated)"
```

---

## Phase 9 — Delete obsolete types

### Task 9.1: Delete store + warmup hosted service

**Files to delete:**
- `src/Humans.Application/Interfaces/Stores/IProfileStore.cs`
- `src/Humans.Infrastructure/Stores/ProfileStore.cs`
- `src/Humans.Infrastructure/HostedServices/ProfileStoreWarmupHostedService.cs`

- [ ] **Step 1: Remove DI registration**

In `InfrastructureServiceCollectionExtensions.cs`:
- Remove: `services.AddSingleton<IProfileStore, ProfileStore>();`
- Remove: `services.AddHostedService<ProfileStoreWarmupHostedService>();`

- [ ] **Step 2: Delete the files**

```bash
git rm src/Humans.Application/Interfaces/Stores/IProfileStore.cs \
       src/Humans.Infrastructure/Stores/ProfileStore.cs \
       src/Humans.Infrastructure/HostedServices/ProfileStoreWarmupHostedService.cs
```

Leave `src/Humans.Application/Interfaces/Stores/IApplicationStore.cs` (Governance's store — removed in #533's scope, not this PR).

- [ ] **Step 3: Build**

```bash
dotnet build Humans.slnx
```

Expected: clean. If anything else referenced `IProfileStore` or `CachedProfile` from the store, fix the references or remove them.

- [ ] **Step 4: Delete CachedProfile + CachedVolunteerEntry types**

These live inside `IProfileStore.cs` (already deleted above). If any standalone definitions remain (e.g., in `IProfileService.cs`), remove them. Search:

```bash
grep -rn "CachedProfile\|CachedVolunteerEntry" src/ tests/
```

Expected after fix: zero matches. Any remaining references are either tests or consumers not yet updated — fix inline.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Delete IProfileStore, ProfileStore, warmup hosted service, CachedProfile"
```

### Task 9.2: Delete VolunteerHistoryService + repository + decorator

**Files to delete:**
- `src/Humans.Application/Interfaces/IVolunteerHistoryService.cs`
- `src/Humans.Application/Services/Profile/VolunteerHistoryService.cs`
- `src/Humans.Application/Interfaces/Repositories/IVolunteerHistoryRepository.cs`
- `src/Humans.Infrastructure/Repositories/VolunteerHistoryRepository.cs`
- `src/Humans.Infrastructure/Services/Profiles/CachingVolunteerHistoryService.cs`
- `tests/Humans.Application.Tests/Services/VolunteerHistoryServiceTests.cs` (and any related)

- [ ] **Step 1: Remove DI registrations**

In `InfrastructureServiceCollectionExtensions.cs`:
- Remove: `services.AddScoped<IVolunteerHistoryService, VolunteerHistoryService>();`
- Remove: `services.Decorate<IVolunteerHistoryService, CachingVolunteerHistoryService>();`
- Remove: `services.AddScoped<IVolunteerHistoryRepository, VolunteerHistoryRepository>();`

- [ ] **Step 2: Audit callers**

```bash
grep -rn "IVolunteerHistoryService\|VolunteerHistoryService\b" src/ tests/
```

Expected callers (to be updated to `IProfileService.SaveCVEntriesAsync` or to read `FullProfile.CVEntries`): `ProfileCardViewComponent.cs`, Profile/Edit controller action, possibly others. For each:
  - Read path → replace with `FullProfile.CVEntries`.
  - Write path → replace with `await _profileService.SaveCVEntriesAsync(userId, entries, ct)`.

Each replacement is a separate commit inside this task — leave one file at a time dirty, commit, move on.

- [ ] **Step 3: Delete the files after callers are clean**

```bash
git rm src/Humans.Application/Interfaces/IVolunteerHistoryService.cs \
       src/Humans.Application/Services/Profile/VolunteerHistoryService.cs \
       src/Humans.Application/Interfaces/Repositories/IVolunteerHistoryRepository.cs \
       src/Humans.Infrastructure/Repositories/VolunteerHistoryRepository.cs \
       src/Humans.Infrastructure/Services/Profiles/CachingVolunteerHistoryService.cs \
       tests/Humans.Application.Tests/Services/VolunteerHistoryServiceTests.cs
```

- [ ] **Step 4: Build + test + commit**

```bash
dotnet build Humans.slnx
dotnet test Humans.slnx
git add -A
git commit -m "Delete VolunteerHistoryService, fold CV entries into IProfileService"
```

Expected: clean build, all tests pass. If test files outside `Humans.Application.Tests/Services/VolunteerHistoryServiceTests.cs` still reference the deleted types, delete or rewrite them.

---

## Phase 10 — Update FullProfile consumers

### Task 10.1: UserAvatarViewComponent

**Files:**
- Modify: `src/Humans.Web/ViewComponents/UserAvatarViewComponent.cs`

- [ ] **Step 1: Replace the `sync ?? await async` pattern with a single `await GetFullProfileAsync`**

```csharp
var cached = await _profileService.GetFullProfileAsync(userId);
if (cached is not null)
{
    displayName = cached.DisplayName;
    profilePictureUrl = ResolveAvatarUrl(cached);
}
// else-branch for onboarding/guest users unchanged
```

If the existing `ResolveAvatarUrl(cached)` expects `CachedProfile`, update its parameter type to `FullProfile`.

- [ ] **Step 2: Build + smoke**

```bash
dotnet build Humans.slnx
```

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/ViewComponents/UserAvatarViewComponent.cs
git commit -m "UserAvatarViewComponent reads FullProfile via ValueTask path"
```

### Task 10.2: HumanLinkTagHelper

**Files:**
- Modify: `src/Humans.Web/TagHelpers/HumanLinkTagHelper.cs`

- [ ] **Step 1: Replace call site**

Line 78: `var cached = await _profileService.GetCachedProfileAsync(UserId);` → `var cached = await _profileService.GetFullProfileAsync(UserId);`

- [ ] **Step 2: Build + commit**

```bash
dotnet build Humans.slnx
git add src/Humans.Web/TagHelpers/HumanLinkTagHelper.cs
git commit -m "HumanLinkTagHelper reads FullProfile"
```

### Task 10.3: ProfileCardViewComponent

**Files:**
- Modify: `src/Humans.Web/ViewComponents/ProfileCardViewComponent.cs`
- Modify: `src/Humans.Web/Views/Shared/Components/ProfileCard/Default.cshtml`

- [ ] **Step 1: Drop IVolunteerHistoryService dependency**

Remove `IVolunteerHistoryService _volunteerHistoryService` field and constructor parameter.

- [ ] **Step 2: Load via FullProfile**

Replace the existing `_profileService.GetProfileAsync(userId)` + separate VolunteerHistory fetch with:

```csharp
var full = await _profileService.GetFullProfileAsync(userId);
// plus any raw Profile you need for fields not on FullProfile
var profile = await _profileService.GetProfileAsync(userId);
```

Update the view-model construction to pull `CVEntries` from `full` (if not null). Map `CVEntry.Date` to the view model's `FormattedDate` property.

- [ ] **Step 3: Build + run view tests if any**

```bash
dotnet build Humans.slnx
```

- [ ] **Step 4: Smoke test in browser**

Start the preview env (or local `dotnet run --project src/Humans.Web`) and load a profile page, an admin human detail page, and any page that renders ProfileCard. Verify CV entries render with dates.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/ViewComponents/ProfileCardViewComponent.cs \
        src/Humans.Web/Views/Shared/Components/ProfileCard/Default.cshtml
git commit -m "ProfileCardViewComponent reads CVEntries from FullProfile"
```

### Task 10.4: Profile/Edit controller action (CV-entry write path)

**Files:**
- Modify: `src/Humans.Web/Controllers/ProfileController.cs` (or wherever the Edit action handles CV entries)

- [ ] **Step 1: Locate the action**

```bash
grep -rn "VolunteerHistoryService\|ReconcileEntriesAsync" src/Humans.Web/
```

- [ ] **Step 2: Replace call**

`await _volunteerHistoryService.ReconcileEntriesAsync(profileId, entries, ct);` → `await _profileService.SaveCVEntriesAsync(userId, entries.Select(e => new CVEntry(e.Date, e.EventName, e.Description)).ToList(), ct);`

Adjust the view-model → CVEntry mapping as appropriate.

- [ ] **Step 3: Build + commit**

```bash
dotnet build Humans.slnx
git add src/Humans.Web/Controllers/ProfileController.cs
git commit -m "Profile/Edit uses SaveCVEntriesAsync for volunteer history"
```

### Task 10.5: Final grep sweep for lingering references

- [ ] **Step 1: Grep**

```bash
grep -rn "CachedProfile\|CachedVolunteerEntry\|IVolunteerHistoryService\|VolunteerHistoryService\|IProfileStore\|ProfileStore\b\|InvalidateCacheAsync" src/ tests/ docs/
```

Expected: zero matches in `src/` and `tests/`. Documentation hits (`docs/`) are addressed in Phase 12.

- [ ] **Step 2: If any src/tests match remain, fix them — commit separately.**

---

## Phase 11 — Architecture tests

### Task 11.1: Update ProfileArchitectureTests

**Files:**
- Modify: `tests/Humans.Application.Tests/Architecture/ProfileArchitectureTests.cs`

- [ ] **Step 1: Remove store-related assertions**

Delete or rename any test that asserts `ProfileService` takes `IProfileStore`. Equivalent of `GovernanceArchitectureTests.ApplicationDecisionService_TakesRepositoryAndStore` — becomes `ProfileService_TakesRepository`.

- [ ] **Step 2: Add new assertions**

```csharp
[Fact]
public void ProfileService_ConstructorTakesNoStoreType()
{
    var ctor = typeof(ProfileService).GetConstructors().Single();
    var storeParam = ctor.GetParameters()
        .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
            .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

    storeParam.Should().BeNull(
        because: "Application services must not depend on store abstractions (design-rules §15 as amended 2026-04-20)");
}

[Fact]
public void CachingProfileService_ImplementsBothInterfaces()
{
    typeof(CachingProfileService).Should().Implement<IProfileService>();
    typeof(CachingProfileService).Should().Implement<IFullProfileInvalidator>();
}
```

- [ ] **Step 2b: Keep existing no-DbContext / no-IMemoryCache assertions.**

- [ ] **Step 3: Run**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~ProfileArchitectureTests"
```

Expected: all pass.

- [ ] **Step 4: Commit**

```bash
git add tests/Humans.Application.Tests/Architecture/ProfileArchitectureTests.cs
git commit -m "Architecture tests: enforce no-store and invalidator implementation"
```

---

## Phase 12 — Design-rules.md §15 rewrite

### Task 12.1: Rewrite §15

**Files:**
- Modify: `docs/architecture/design-rules.md`

- [ ] **Step 1: Replace §15 "repo + store + decorator + warmup" description**

Locate §15 (it's where the old pattern was written up; look for headings like "Store layer" or references to `IProfileStore`). Replace with the Target pattern + Rules content from the design spec (`docs/superpowers/specs/2026-04-20-pr235-cache-collapse-design.md`). Do not copy the whole spec — just the definition sections verbatim.

- [ ] **Step 2: Update flow diagram**

Replace any `ProfileRepository, ProfileStore` line in diagrams with `ProfileRepository` alone. Replace any decorator-wraps-store text with decorator-owns-dict.

- [ ] **Step 3: Update example constructor**

Line 314 (on main): `public class CampService(ICampRepository repo, IProfileStore profileStore)` — change to: `public class CampService(ICampRepository repo, IProfileService profileService)`. The general rule changes: cross-section reads go through service interfaces, not stores.

- [ ] **Step 4: Keep §15c (nav-property stripping) as-is.**

- [ ] **Step 5: Commit**

```bash
git add docs/architecture/design-rules.md
git commit -m "§15 rewrite: cache lives in the decorator, no store abstraction"
```

---

## Phase 13 — Amend PR body + smoke test + push

### Task 13.1: Run final verification gate

- [ ] **Step 1: Full build + test + format**

```bash
dotnet build Humans.slnx
dotnet test Humans.slnx
dotnet format Humans.slnx --verify-no-changes
```

Expected: all clean. Baseline test failure from the pre-existing DataProtection bug stays (note the specific number).

- [ ] **Step 2: Browser smoke test on preview env**

Push branch to origin (force-with-lease) to trigger the preview env:

```bash
git push origin sprint/2026-04-16/issue-504 --force-with-lease
```

Coolify builds and deploys to `{pr_id}.n.burn.camp`. Walk through:
- Profile view (own) — avatar renders, name correct.
- Profile edit — submit a name / bio / city change, verify it persists.
- Edit volunteer history — add, edit, delete entries; verify they persist.
- Admin human detail page for another user — avatar, name, CV entries.
- Profile search — results show avatars.
- Team page — 30 avatars render (warm-cache path).
- Log in → log out → log in (forces refresh-from-cache path).
- Trigger an account deletion job in admin — verify the decorator removes the entry.

### Task 13.2: Amend PR body

- [ ] **Step 1: Edit the PR description on peter's fork**

```bash
gh pr view 235 --json body | jq -r .body > /tmp/pr235-body.md
# Edit /tmp/pr235-body.md:
#   Replace the "Store layer" paragraph with:
#     "**Caching layer:** CachingProfileService owns a singleton
#      ConcurrentDictionary<Guid, FullProfile>, lazy-warmed on demand. No
#      separate store abstraction, no warmup hosted service."
#   Replace the "Decorator layer" paragraph with:
#     "CachingProfileService implements IProfileService (via Scrutor
#      Decorate<>) and IFullProfileInvalidator from the same singleton
#      instance."
#   Insert an "Architectural correction" block linking to:
#     docs/superpowers/specs/2026-04-20-pr235-cache-collapse-design.md

gh pr edit 235 --body-file /tmp/pr235-body.md
```

- [ ] **Step 2: Leave "Closes #504" and the test plan block intact.**

### Task 13.3: Final push

- [ ] **Step 1: Verify branch state**

```bash
git log --oneline origin/sprint/2026-04-16/issue-504..HEAD
```

Expected: a linear sequence of commits from this plan's tasks.

- [ ] **Step 2: Push**

```bash
git push origin sprint/2026-04-16/issue-504 --force-with-lease
```

- [ ] **Step 3: Wait for CI green on GitHub Actions**

If CI fails, investigate and fix on the branch (don't cherry-pick to main until CI passes — per project memory).

- [ ] **Step 4: Clean up worktree**

After PR merges to `peterdrier/Humans main`:

```bash
cd H:/source/Humans
git worktree remove .worktrees/pr235-cache-collapse
git branch -d sprint/2026-04-16/issue-504-cache-collapse   # if the intermediate branch was used
```

---

## Verification matrix (run after each phase)

| After phase | Expected state |
|---|---|
| 0 | Worktree exists, baseline build clean, tests at known baseline (likely 1010/1011). |
| 1 | `FullProfile` and `CVEntry` compile but are unused; no test changes. |
| 2 | `GetFullProfileAsync` on base service tested + passing; decorator pass-through compiles. |
| 3 | Decorator dict cache works for reads; decorator tests pass; IProfileStore + IMemoryCache gone from decorator. |
| 4 | `SaveCVEntriesAsync` path works end-to-end through a real DbContext integration test. |
| 5 | Write-refresh tested; dict stays consistent after Profile writes. |
| 6 | IFullProfileInvalidator exists, decorator implements it, both read/delete invalidate paths tested. |
| 7 | All four external callers now use IFullProfileInvalidator; their tests updated. |
| 8 | `InvalidateCacheAsync` removed from IProfileService; grep returns zero hits in src/. |
| 9 | Store, warmup, VolunteerHistoryService, repository, decorator, and their tests all deleted; build clean. |
| 10 | UserAvatarViewComponent, HumanLinkTagHelper, ProfileCardViewComponent, Profile/Edit all migrated; browser smoke test clean. |
| 11 | Architecture tests updated; grep for `CachedProfile\|IProfileStore\|InvalidateCacheAsync` in src/tests returns empty. |
| 12 | design-rules.md §15 reflects new pattern. |
| 13 | PR body amended, CI green, branch force-pushed. |

---

## Notes on verifying the actual branch state at plan-execution time

The branch has moved since this plan was drafted (commits through `4a38fb32` are visible). Before starting:

- Re-run `git log origin/sprint/2026-04-16/issue-504 --oneline -20` and compare against the commits listed in this plan's Background. If later commits have landed, the plan may need small amendments (particularly in Phase 7 line-number references, which shift).
- Re-read `CachingProfileService.cs` at the branch head to confirm which mutation methods it currently intercepts; each one needs the `RefreshEntryAsync` hook (Task 5.2 step 2 refers to "existing write paths" — audit the actual list).
- Re-read `IProfileService.cs` at the branch head to confirm no other `GetCached*` variants crept back in.

These are small calibrations; the overall shape of the plan holds.
