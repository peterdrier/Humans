# UserInfo-driven Admin Stats, Debug Table, and Ticket Venn — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the misleading `/Admin` "Active humans 979" card with three accurate stats sourced from the in-memory `UserInfo` cache, add a `/Users/Admin/Debug` flat table for cache spot-checking, and replace the redundant `/Tickets` coverage card + unbounded donut with a 3-set Venn + UpSet of Users/Profiles/Tickets.

**Architecture:** Single source of truth — `IUserService.GetAllUserInfos()` returns a snapshot of the in-memory `UserInfo` dictionary held by `CachingUserService`. All three deliverables read from this snapshot only. `UserInfo` is extended with `CommunicationPreferences` so Marketing opt-in lives on the cached god-object; the SaveChanges interceptor catches preference writes for invalidation.

**Tech Stack:** ASP.NET Core MVC + Razor, EF Core (Postgres) for persistence (untouched on this path), NodaTime, xUnit + NSubstitute (existing test patterns), `@upsetjs/venn.js` + `@upsetjs/bundle` (new JS libs for the Tickets page).

**Worktree:** `H:\source\Humans\.worktrees\userinfo-debug-and-venn` (branch `feat/userinfo-debug-and-venn`, off `origin/main`). All commands assume this is the working directory.

**Build command:** `dotnet build Humans.slnx -v quiet`
**Test command:** `dotnet test Humans.slnx -v quiet`
(`-v quiet` is required per `memory/process/dotnet-verbosity-quiet.md`.)

**Reference spec:** `docs/superpowers/specs/2026-05-14-userinfo-debug-and-venn-design.md`

---

## File Structure

### Modified

- `src/Humans.Application/UserInfo.cs` — add `CommunicationPreferenceInfo` record, `CommunicationPreferences` field, `MarketingOptedOut` and `HasTicket` accessors, extend `Create()` signature.
- `src/Humans.Application/Interfaces/Users/IUserService.cs` — add `GetAllUserInfos()`, bump `[SurfaceBudget(32)]` → `33`, add history line.
- `src/Humans.Application/Services/Users/UserService.cs` — inner `GetUserInfoAsync` loads `CommunicationPreference` and passes through.
- `src/Humans.Application/Interfaces/Repositories/ICommunicationPreferenceRepository.cs` — add `GetAllAsync(ct)`.
- `src/Humans.Infrastructure/Repositories/Profiles/CommunicationPreferenceRepository.cs` — implement `GetAllAsync(ct)`.
- `src/Humans.Infrastructure/Services/Users/CachingUserService.cs` — inject `ICommunicationPreferenceRepository`, extend `WarmAllAsync` and `RefreshEntryAsync`, add `GetAllUserInfos()`.
- `src/Humans.Infrastructure/Data/UserInfoSaveChangesInterceptor.cs` — handle `CommunicationPreference` entity.
- `src/Humans.Web/Models/AdminDashboardViewModel.cs` — replace `ActiveHumans` with `TotalUsers` + `ActiveProfileUsers` + `TicketHolders`.
- `src/Humans.Web/Controllers/AdminController.cs` — `Index` uses `_userService.GetAllUserInfos()` snapshot.
- `src/Humans.Web/Views/Shared/_DashboardStats.cshtml` — three new stat tiles.
- `src/Humans.Web/Views/Admin/Index.cshtml` — top-of-page subtitle uses the new fields.
- `src/Humans.Web/Models/TicketViewModels.cs` — replace `TotalActiveVolunteers` / `VolunteersWithTickets` / `VolunteerCoveragePercent` / `ParticipationNotAttending` / `ParticipationNoTicket` / `ParticipationHasTicket` with `SetMembership` data.
- `src/Humans.Web/Controllers/TicketController.cs` — `Index` populates `SetMembership` from `_userService.GetAllUserInfos()`.
- `src/Humans.Web/Views/Ticket/Index.cshtml` — remove old coverage card and donut; add Venn + UpSet panels and scripts.
- `src/Humans.Web/ViewComponents/AdminNavTree.cs` — add `All users (debug)` entry to the `Diagnostics` group.

### Created

- `src/Humans.Web/Controllers/UsersAdminDebugController.cs` — new controller.
- `src/Humans.Web/Models/UsersDebugViewModel.cs` — view model + row record.
- `src/Humans.Web/Views/UsersAdminDebug/Index.cshtml` — debug table view.
- `tests/Humans.Application.Tests/UserInfoTests.cs` — accessor unit tests.
- `tests/Humans.Web.Tests/Controllers/UsersAdminDebugControllerTests.cs` — controller tests.

---

## Task 1: `CommunicationPreferenceInfo` record + `CommunicationPreferences` field on `UserInfo`

**Files:**
- Modify: `src/Humans.Application/UserInfo.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Humans.Application.Tests/UserInfoTests.cs`:

```csharp
using FluentAssertions;
using Humans.Application;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests;

public class UserInfoTests
{
    private static User MinimalUser(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        DisplayName = "Test",
        PreferredLanguage = "en",
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        GoogleEmailStatus = GoogleEmailStatus.Unknown,
    };

    [Fact]
    public void Create_carries_communication_preferences_projection()
    {
        var userId = Guid.NewGuid();
        var prefs = new[]
        {
            new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = MessageCategory.Marketing,
                OptedOut = false,
                InboxEnabled = true,
                UpdatedAt = Instant.FromUtc(2026, 4, 1, 0, 0),
                UpdateSource = "Profile",
                SubscribedAt = Instant.FromUtc(2026, 4, 1, 0, 0),
            },
            new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = MessageCategory.Governance,
                OptedOut = true,
                InboxEnabled = false,
                UpdatedAt = Instant.FromUtc(2026, 4, 2, 0, 0),
                UpdateSource = "MagicLink",
                SubscribedAt = null,
            },
        };

        var info = UserInfo.Create(
            user: MinimalUser(userId),
            userEmails: Array.Empty<UserEmail>(),
            eventParticipations: Array.Empty<EventParticipation>(),
            externalLogins: Array.Empty<(string, string)>(),
            profile: null,
            contactFields: Array.Empty<ContactField>(),
            profileLanguages: Array.Empty<ProfileLanguage>(),
            volunteerHistory: Array.Empty<VolunteerHistoryEntry>(),
            communicationPreferences: prefs);

        info.CommunicationPreferences.Should().HaveCount(2);
        info.CommunicationPreferences.Select(c => c.Category)
            .Should().Equal(MessageCategory.Governance, MessageCategory.Marketing);
        info.CommunicationPreferences[1].OptedOut.Should().BeFalse();
        info.CommunicationPreferences[1].UpdateSource.Should().Be("Profile");
    }
}
```

(The expected order is `Category` ascending — `Governance` (3) before `Marketing` (7) per the `MessageCategory` enum order.)

- [ ] **Step 2: Run test to verify it fails (and the test file at least compiles)**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UserInfoTests"`

Expected: compile error on the `communicationPreferences:` argument (parameter does not exist on `UserInfo.Create`).

- [ ] **Step 3: Add `CommunicationPreferenceInfo` record**

In `src/Humans.Application/UserInfo.cs`, add after the existing `VolunteerHistoryInfo` record (around line 48):

```csharp
/// <summary>
/// Compact projection of a <see cref="CommunicationPreference"/> row.
/// </summary>
public sealed record CommunicationPreferenceInfo(
    Guid Id,
    MessageCategory Category,
    bool OptedOut,
    bool InboxEnabled,
    Instant UpdatedAt,
    string UpdateSource,
    Instant? SubscribedAt);
```

- [ ] **Step 4: Add `CommunicationPreferences` field to `UserInfo`**

In the `UserInfo` primary constructor (around line 180), add the new parameter at the end of the existing parameter list (after `Profile`):

```csharp
public sealed record UserInfo(
    // ... existing parameters unchanged ...
    ProfileInfo? Profile,
    IReadOnlyList<CommunicationPreferenceInfo> CommunicationPreferences)
{
    // ... existing body ...
}
```

Make sure the parameter has a comma after `ProfileInfo? Profile`.

- [ ] **Step 5: Extend `UserInfo.Create()`**

In the same file, find `public static UserInfo Create(`. Add a new parameter at the end:

```csharp
public static UserInfo Create(
    User user,
    IReadOnlyList<UserEmail> userEmails,
    IReadOnlyList<EventParticipation> eventParticipations,
    IReadOnlyList<(string Provider, string ProviderKey)> externalLogins,
    Profile? profile,
    IReadOnlyList<ContactField> contactFields,
    IReadOnlyList<ProfileLanguage> profileLanguages,
    IReadOnlyList<VolunteerHistoryEntry> volunteerHistory,
    IReadOnlyList<CommunicationPreference> communicationPreferences)
{
    // ... existing body ...
}
```

Inside the method body, before the final `return new UserInfo(...)`, add:

```csharp
var communicationPreferenceInfos = communicationPreferences
    .OrderBy(c => c.Category)
    .Select(c => new CommunicationPreferenceInfo(
        c.Id, c.Category, c.OptedOut, c.InboxEnabled,
        c.UpdatedAt, c.UpdateSource, c.SubscribedAt))
    .ToList();
```

In the `return new UserInfo(...)` block, add the new argument at the end (after `Profile: profileInfo`):

```csharp
        Profile: profileInfo,
        CommunicationPreferences: communicationPreferenceInfos);
```

- [ ] **Step 6: Fix existing callers of `UserInfo.Create()`**

There are three known callers; each must pass the new parameter. Run `dotnet build Humans.slnx -v quiet` and let the compiler list them. Expected three error locations:

- `src/Humans.Application/Services/Users/UserService.cs` (`GetUserInfoAsync`)
- `src/Humans.Infrastructure/Services/Users/CachingUserService.cs` (`RefreshEntryAsync` and `WarmAllAsync`)

For each one, pass `Array.Empty<CommunicationPreference>()` as the new last argument for now. Tasks 4 and 5 wire them properly.

```csharp
// In UserService.GetUserInfoAsync, end of the return:
return UserInfo.Create(
    user, userEmails, participations, externalLogins,
    profile, contactFields, languages, volunteerHistory,
    Array.Empty<CommunicationPreference>());
```

```csharp
// In CachingUserService.RefreshEntryAsync:
_byUserId[userId] = UserInfo.Create(
    user, userEmails, participations, externalLogins,
    profile, contactFields, languages, volunteerHistory,
    Array.Empty<CommunicationPreference>());
```

```csharp
// In CachingUserService.WarmAllAsync (inside the foreach):
_byUserId[user.Id] = UserInfo.Create(
    user, emails, participations, logins,
    profile, contactFields, languages, volunteerHistory,
    Array.Empty<CommunicationPreference>());
```

- [ ] **Step 7: Run build and test**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UserInfoTests"`
Expected: `Create_carries_communication_preferences_projection` PASSES.

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Application/UserInfo.cs \
        src/Humans.Application/Services/Users/UserService.cs \
        src/Humans.Infrastructure/Services/Users/CachingUserService.cs \
        tests/Humans.Application.Tests/UserInfoTests.cs
git commit -m "feat(userinfo): add CommunicationPreferences to UserInfo projection"
```

---

## Task 2: `UserInfo.MarketingOptedOut` and `UserInfo.HasTicket` accessors

**Files:**
- Modify: `src/Humans.Application/UserInfo.cs`
- Modify: `tests/Humans.Application.Tests/UserInfoTests.cs`

- [ ] **Step 1: Add failing tests**

Append to `tests/Humans.Application.Tests/UserInfoTests.cs`:

```csharp
    [Fact]
    public void MarketingOptedOut_is_null_when_no_marketing_pref()
    {
        var info = UserInfo.Create(
            MinimalUser(),
            Array.Empty<UserEmail>(),
            Array.Empty<EventParticipation>(),
            Array.Empty<(string, string)>(),
            profile: null,
            Array.Empty<ContactField>(),
            Array.Empty<ProfileLanguage>(),
            Array.Empty<VolunteerHistoryEntry>(),
            Array.Empty<CommunicationPreference>());

        info.MarketingOptedOut.Should().BeNull();
    }

    [Fact]
    public void MarketingOptedOut_reflects_pref_when_present()
    {
        var userId = Guid.NewGuid();
        var prefs = new[]
        {
            new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = MessageCategory.Marketing,
                OptedOut = true,
                InboxEnabled = true,
                UpdatedAt = Instant.FromUtc(2026, 4, 1, 0, 0),
                UpdateSource = "Profile",
            },
        };

        var info = UserInfo.Create(
            MinimalUser(userId),
            Array.Empty<UserEmail>(),
            Array.Empty<EventParticipation>(),
            Array.Empty<(string, string)>(),
            profile: null,
            Array.Empty<ContactField>(),
            Array.Empty<ProfileLanguage>(),
            Array.Empty<VolunteerHistoryEntry>(),
            prefs);

        info.MarketingOptedOut.Should().BeTrue();
    }

    [Fact]
    public void HasTicket_true_when_any_participation_is_Ticketed_or_Attended()
    {
        var participations = new[]
        {
            new EventParticipation
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                Year = 2026,
                Status = ParticipationStatus.Ticketed,
                Source = ParticipationSource.TicketSync,
            }
        };

        var info = UserInfo.Create(
            MinimalUser(),
            Array.Empty<UserEmail>(),
            participations,
            Array.Empty<(string, string)>(),
            profile: null,
            Array.Empty<ContactField>(),
            Array.Empty<ProfileLanguage>(),
            Array.Empty<VolunteerHistoryEntry>(),
            Array.Empty<CommunicationPreference>());

        info.HasTicket.Should().BeTrue();
    }

    [Fact]
    public void HasTicket_false_when_only_NotAttending_or_no_participations()
    {
        var participations = new[]
        {
            new EventParticipation
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                Year = 2026,
                Status = ParticipationStatus.NotAttending,
                Source = ParticipationSource.UserDeclared,
            }
        };

        var info = UserInfo.Create(
            MinimalUser(),
            Array.Empty<UserEmail>(),
            participations,
            Array.Empty<(string, string)>(),
            profile: null,
            Array.Empty<ContactField>(),
            Array.Empty<ProfileLanguage>(),
            Array.Empty<VolunteerHistoryEntry>(),
            Array.Empty<CommunicationPreference>());

        info.HasTicket.Should().BeFalse();
    }
```

- [ ] **Step 2: Run tests to confirm they fail**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UserInfoTests"`
Expected: compile error — `MarketingOptedOut` and `HasTicket` not defined.

- [ ] **Step 3: Add the accessors**

In `src/Humans.Application/UserInfo.cs`, inside the `UserInfo` record body (after `AllVerifiedEmails`, before the `Create` static method), add:

```csharp
    /// <summary>
    /// Marketing-category opt-in tri-state: null when no preference row exists
    /// (e.g., user imported from an external source who never hit the prefs
    /// flow), true when opted out, false when opted in.
    /// </summary>
    public bool? MarketingOptedOut => CommunicationPreferences
        .Where(c => c.Category == MessageCategory.Marketing)
        .Select(c => (bool?)c.OptedOut)
        .FirstOrDefault();

    /// <summary>
    /// True when the user has at least one event participation in the
    /// <see cref="ParticipationStatus.Ticketed"/> or
    /// <see cref="ParticipationStatus.Attended"/> state — i.e., currently
    /// holds a ticket or has been checked in. Matches the predicate used by
    /// the Tickets dashboard so the admin stats and the Venn agree on
    /// "ticket holder".
    /// </summary>
    public bool HasTicket => EventParticipations.Any(p =>
        p.Status == ParticipationStatus.Ticketed ||
        p.Status == ParticipationStatus.Attended);
```

- [ ] **Step 4: Run tests to confirm they pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UserInfoTests"`
Expected: all 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/UserInfo.cs tests/Humans.Application.Tests/UserInfoTests.cs
git commit -m "feat(userinfo): add MarketingOptedOut and HasTicket accessors"
```

---

## Task 3: `ICommunicationPreferenceRepository.GetAllAsync` + implementation

**Files:**
- Modify: `src/Humans.Application/Interfaces/Repositories/ICommunicationPreferenceRepository.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Profiles/CommunicationPreferenceRepository.cs`

- [ ] **Step 1: Add interface method**

In `src/Humans.Application/Interfaces/Repositories/ICommunicationPreferenceRepository.cs`, add after `GetByUserIdReadOnlyAsync`:

```csharp
    /// <summary>
    /// Returns every <c>communication_preferences</c> row, read-only. Used by
    /// the <see cref="UserInfo"/> cache warm path to bulk-load preferences
    /// once at startup. At ~500 users × ~7 categories the row count is small
    /// — a single SELECT is cheaper than per-user round-trips.
    /// </summary>
    Task<IReadOnlyList<CommunicationPreference>> GetAllAsync(CancellationToken ct = default);
```

- [ ] **Step 2: Build to confirm the implementation is now missing**

Run: `dotnet build Humans.slnx -v quiet`
Expected: error — `CommunicationPreferenceRepository` doesn't implement `GetAllAsync`.

- [ ] **Step 3: Implement in repository**

Open `src/Humans.Infrastructure/Repositories/Profiles/CommunicationPreferenceRepository.cs`. Add this method (match the style of the existing `GetByUserIdReadOnlyAsync`):

```csharp
    public async Task<IReadOnlyList<CommunicationPreference>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CommunicationPreferences
            .AsNoTracking()
            .ToListAsync(ct);
    }
```

(If the existing methods use a different DbContext-acquisition pattern, match that pattern instead — `_factory.CreateDbContextAsync` is the most common one in this codebase. Read the existing `GetByUserIdReadOnlyAsync` in this same file first if uncertain.)

- [ ] **Step 4: Run build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 5: Run repository tests if they exist**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~CommunicationPreferenceRepository"`
Expected: tests pass or no tests found (acceptable — `GetAllAsync` is a thin SELECT).

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Interfaces/Repositories/ICommunicationPreferenceRepository.cs \
        src/Humans.Infrastructure/Repositories/Profiles/CommunicationPreferenceRepository.cs
git commit -m "feat(repo): add ICommunicationPreferenceRepository.GetAllAsync"
```

---

## Task 4: Inner `UserService.GetUserInfoAsync` loads preferences

**Files:**
- Modify: `src/Humans.Application/Services/Users/UserService.cs`

- [ ] **Step 1: Inject `ICommunicationPreferenceRepository`**

In `src/Humans.Application/Services/Users/UserService.cs`, add a field next to `_contactFieldRepo` (around line 48):

```csharp
    private readonly ICommunicationPreferenceRepository _communicationPreferenceRepo;
```

Add a constructor parameter (alphabetically after `contactFieldRepo`, before `fullProfileInvalidator`):

```csharp
    public UserService(
        IUserRepository repo,
        IUserEmailRepository userEmailRepo,
        IProfileRepository profileRepo,
        IContactFieldRepository contactFieldRepo,
        ICommunicationPreferenceRepository communicationPreferenceRepo,
        IFullProfileInvalidator fullProfileInvalidator,
        IAdminAuthorizationService adminAuthorization,
        IClock clock,
        ILogger<UserService> logger)
    {
        _repo = repo;
        _userEmailRepo = userEmailRepo;
        _profileRepo = profileRepo;
        _contactFieldRepo = contactFieldRepo;
        _communicationPreferenceRepo = communicationPreferenceRepo;
        _fullProfileInvalidator = fullProfileInvalidator;
        _adminAuthorization = adminAuthorization;
        _clock = clock;
        _logger = logger;
    }
```

- [ ] **Step 2: Load preferences inside `GetUserInfoAsync`**

In the same file, find `GetUserInfoAsync` (around line 74). Inside, after the profile/contact-fields/languages/volunteerHistory load block, add:

```csharp
        var communicationPreferences = await _communicationPreferenceRepo
            .GetByUserIdReadOnlyAsync(userId, ct);
```

Then change the final `return UserInfo.Create(...)` to pass `communicationPreferences` instead of `Array.Empty<CommunicationPreference>()`:

```csharp
        return UserInfo.Create(
            user, userEmails, participations, externalLogins,
            profile, contactFields, languages, volunteerHistory,
            communicationPreferences);
```

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

Note: this will likely also require updating the DI registration if `UserService` is registered with explicit constructor injection. Search for `AddScoped<IUserService` / `AddKeyedScoped<IUserService`:

Run: `grep -rn "AddScoped<IUserService\|AddKeyedScoped<IUserService\|services\.AddScoped<.*UserService>" src/`

Most likely the DI registration is on `services.AddScoped<IUserService, UserService>()` — Microsoft.Extensions.DependencyInjection resolves the new dependency automatically. If a manual `new UserService(...)` exists anywhere, fix those call sites by adding the new parameter.

- [ ] **Step 4: Run tests**

Run: `dotnet test Humans.slnx -v quiet`
Expected: all tests pass. If any test instantiates `UserService` directly with the old constructor shape, update those tests with a mock `ICommunicationPreferenceRepository`.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Services/Users/UserService.cs
git commit -m "feat(userinfo): inner UserService loads communication preferences"
```

---

## Task 5: `CachingUserService` warm + refresh load preferences

**Files:**
- Modify: `src/Humans.Infrastructure/Services/Users/CachingUserService.cs`

- [ ] **Step 1: Inject `ICommunicationPreferenceRepository`**

In `src/Humans.Infrastructure/Services/Users/CachingUserService.cs`, add a field after `_contactFieldRepository`:

```csharp
    private readonly ICommunicationPreferenceRepository _communicationPreferenceRepository;
```

Add a constructor parameter after `contactFieldRepository`:

```csharp
    public CachingUserService(
        IUserRepository userRepository,
        IUserEmailRepository userEmailRepository,
        IProfileRepository profileRepository,
        IContactFieldRepository contactFieldRepository,
        ICommunicationPreferenceRepository communicationPreferenceRepository,
        IServiceScopeFactory scopeFactory,
        ILogger<CachingUserService> logger)
    {
        _userRepository = userRepository;
        _userEmailRepository = userEmailRepository;
        _profileRepository = profileRepository;
        _contactFieldRepository = contactFieldRepository;
        _communicationPreferenceRepository = communicationPreferenceRepository;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }
```

- [ ] **Step 2: Update `RefreshEntryAsync`**

Find `RefreshEntryAsync` (around line 104). After the existing per-user load block (before the `_byUserId[userId] = UserInfo.Create(...)` assignment), add:

```csharp
        var communicationPreferences = await _communicationPreferenceRepository
            .GetByUserIdReadOnlyAsync(userId, ct);
```

Update the `UserInfo.Create()` call to pass `communicationPreferences` instead of `Array.Empty<CommunicationPreference>()`:

```csharp
        _byUserId[userId] = UserInfo.Create(
            user, userEmails, participations, externalLogins,
            profile, contactFields, languages, volunteerHistory,
            communicationPreferences);
```

- [ ] **Step 3: Update `WarmAllAsync`**

Find `WarmAllAsync` (around line 142). After the existing bulk-loads (after `participationsByUser`), add:

```csharp
        var allPreferences = await _communicationPreferenceRepository.GetAllAsync(ct);
        var preferencesByUser = allPreferences
            .GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CommunicationPreference>)g.ToList());
```

Inside the `foreach (var user in users)` loop, after the other per-user lookups, add:

```csharp
            var preferences = preferencesByUser.TryGetValue(user.Id, out var pp)
                ? pp : Array.Empty<CommunicationPreference>();
```

Update the `UserInfo.Create()` call to pass `preferences` instead of `Array.Empty<CommunicationPreference>()`:

```csharp
            _byUserId[user.Id] = UserInfo.Create(
                user, emails, participations, logins,
                profile, contactFields, languages, volunteerHistory,
                preferences);
```

- [ ] **Step 4: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 5: Run tests**

Run: `dotnet test Humans.slnx -v quiet`
Expected: tests pass. If `CachingUserServiceTests` has a setup helper that constructs the service, update it with a mock `ICommunicationPreferenceRepository`.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Infrastructure/Services/Users/CachingUserService.cs \
        tests/Humans.Application.Tests/Services/Users/CachingUserServiceTests.cs
git commit -m "feat(userinfo): cache warm/refresh load communication preferences"
```

---

## Task 6: `IUserService.GetAllUserInfos()` + bump SurfaceBudget

**Files:**
- Modify: `src/Humans.Application/Interfaces/Users/IUserService.cs`
- Modify: `src/Humans.Application/Services/Users/UserService.cs`
- Modify: `src/Humans.Infrastructure/Services/Users/CachingUserService.cs`

- [ ] **Step 1: Add the surface method to the interface**

In `src/Humans.Application/Interfaces/Users/IUserService.cs`, after the existing `GetUserInfoAsync` declaration (around line 41), add:

```csharp
    /// <summary>
    /// Returns a snapshot of every cached <see cref="UserInfo"/>. Issue #703:
    /// the cache is the canonical "everything-about-a-person" source; admin
    /// stat tiles, debug surfaces, and cross-section aggregates read from
    /// this snapshot rather than re-querying the contributing tables. Returns
    /// a new collection per call — the underlying dictionary is mutable and
    /// callers iterate without locking.
    /// </summary>
    IReadOnlyCollection<UserInfo> GetAllUserInfos();
```

Bump the `[SurfaceBudget(32)]` attribute on the interface to `[SurfaceBudget(33)]`. In the XML doc's `<remarks>` block, add a new history bullet at the top of the bullet list:

```csharp
///   <item>32→33 — admin stats + /Users/Admin/Debug + /Tickets Venn: added GetAllUserInfos. Snapshot accessor — the cache is the canonical read-model; all aggregate consumers read from it rather than re-querying the underlying tables.</item>
```

- [ ] **Step 2: Implement on inner `UserService`**

In `src/Humans.Application/Services/Users/UserService.cs`, the inner service does not own the snapshot — it's a no-op delegate to a not-applicable case. Add:

```csharp
    public IReadOnlyCollection<UserInfo> GetAllUserInfos()
    {
        // Inner service is the cache-miss reload path; it never holds a
        // snapshot. The caching decorator's implementation is the real one.
        // This method exists to satisfy the interface; in practice every
        // consumer resolves IUserService and gets the Singleton decorator.
        throw new NotSupportedException(
            "GetAllUserInfos is only meaningful through CachingUserService. " +
            "If this is being called on the inner UserService it indicates a DI " +
            "registration mistake — IUserService should resolve to CachingUserService.");
    }
```

- [ ] **Step 3: Implement on `CachingUserService`**

In `src/Humans.Infrastructure/Services/Users/CachingUserService.cs`, in the `// UserInfo reads` region (after `LoadAndCacheAsync`), add:

```csharp
    /// <inheritdoc cref="IUserService.GetAllUserInfos" />
    public IReadOnlyCollection<UserInfo> GetAllUserInfos() => _byUserId.Values.ToArray();
```

- [ ] **Step 4: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 5: Add a smoke test for the snapshot**

Append to `tests/Humans.Application.Tests/Services/Users/CachingUserServiceTests.cs`:

```csharp
    [Fact]
    public async Task GetAllUserInfos_returns_snapshot_of_cached_entries()
    {
        // Arrange — drive a warm to populate the cache. Test harness setup
        // already wires repositories with N seeded users in BuildSut.
        var sut = BuildSut(out _);
        await sut.WarmAllAsync();

        // Act
        var snapshot = sut.GetAllUserInfos();

        // Assert — every cached entry appears in the snapshot.
        snapshot.Should().NotBeEmpty();
        snapshot.Count.Should().Be(_seededUserCount);
    }
```

(Adjust to whatever `BuildSut` / seeded-user pattern actually exists in `CachingUserServiceTests`. The point is: warm, then `GetAllUserInfos()`, count matches the seeded users.)

- [ ] **Step 6: Run tests**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~CachingUserServiceTests"`
Expected: all pass including the new test.

Also run the full test suite to verify nothing relies on the SurfaceBudget number:

Run: `dotnet test Humans.slnx -v quiet`
Expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Application/Interfaces/Users/IUserService.cs \
        src/Humans.Application/Services/Users/UserService.cs \
        src/Humans.Infrastructure/Services/Users/CachingUserService.cs \
        tests/Humans.Application.Tests/Services/Users/CachingUserServiceTests.cs
git commit -m "feat(userinfo): IUserService.GetAllUserInfos snapshot accessor"
```

---

## Task 7: `UserInfoSaveChangesInterceptor` catches `CommunicationPreference` writes

**Files:**
- Modify: `src/Humans.Infrastructure/Data/UserInfoSaveChangesInterceptor.cs`

- [ ] **Step 1: Add entity case to the switch**

In `src/Humans.Infrastructure/Data/UserInfoSaveChangesInterceptor.cs`, find `CollectAffectedUserIds` (around line 142). Add a new case to the entity switch (after `IdentityUserLogin<Guid> uil:`):

```csharp
                case CommunicationPreference cp:
                    affected.Add(cp.UserId);
                    break;
```

- [ ] **Step 2: Update the XML doc covered-tables list**

In the same file, in the class-level `<remarks>` block, find the bullet list listing covered tables (around lines 23-28). Add a new bullet:

```csharp
///   <item><c>communication_preferences</c> — opt-in/out toggles written directly via <c>ICommunicationPreferenceRepository</c>; rides on the User cache because the prefs collection is part of <c>UserInfo</c>.</item>
```

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds (the `using` for `Humans.Domain.Entities` is already present).

- [ ] **Step 4: Run interceptor tests if they exist**

Run: `grep -rln "UserInfoSaveChangesInterceptor" tests/`

If interceptor tests exist, add a case that writes a `CommunicationPreference` and asserts `IUserInfoInvalidator.InvalidateAsync` was called with the user's id. Match the existing test pattern for the other entities.

If no test file exists, do NOT create one — the interceptor is exercised end-to-end by the cache integration tests already in `CachingUserServiceTests`. Move on.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Data/UserInfoSaveChangesInterceptor.cs
git commit -m "feat(userinfo): interceptor invalidates on CommunicationPreference writes"
```

---

## Task 8: `AdminDashboardViewModel` swap fields

**Files:**
- Modify: `src/Humans.Web/Models/AdminDashboardViewModel.cs`

- [ ] **Step 1: Replace `ActiveHumans` with three new fields**

In `src/Humans.Web/Models/AdminDashboardViewModel.cs`, edit the record:

```csharp
public sealed record AdminDashboardViewModel(
    string GreetingFirstName,
    int TotalUsers,
    int ActiveProfileUsers,
    int TicketHolders,
    int ShiftCoveragePercent,
    int? ShiftFilledOf,
    int? ShiftTotalOf,
    int OpenFeedback,
    IReadOnlyList<DepartmentCoverage> StaffingByDepartment,
    IReadOnlyList<DashboardActivityRow> RecentActivity,
    DashboardApplicationStats AppStats,
    IReadOnlyList<DashboardLanguageCount> LanguageDistribution);
```

(Order matters — `TotalUsers`, `ActiveProfileUsers`, `TicketHolders` replace the single `ActiveHumans` parameter at position 2.)

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: errors at every site that constructs `AdminDashboardViewModel` (likely just `AdminController.Index`) and at every site that reads `Model.ActiveHumans` (likely `_DashboardStats.cshtml` and `Admin/Index.cshtml`). These are fixed in the next tasks.

- [ ] **Step 3: Do not commit alone** — bundle with Task 9 and 10.

---

## Task 9: `AdminController.Index` swap to snapshot

**Files:**
- Modify: `src/Humans.Web/Controllers/AdminController.cs`

- [ ] **Step 1: Add `IUserService` dependency**

`AdminController` does not currently inject `IUserService`. The `Index` action already takes `[FromServices]` deps; add another one:

In `Index`'s parameter list, add `[FromServices] IUserService userService`:

```csharp
    [HttpGet("")]
    [Authorize(Policy = PolicyNames.AnyAdminRole)]
    public async Task<IActionResult> Index(
        [FromServices] IProfileService profileService,
        [FromServices] IShiftManagementService shifts,
        [FromServices] IFeedbackService feedback,
        [FromServices] IAuditViewerService auditViewer,
        [FromServices] IAdminDashboardService adminDashboardService,
        [FromServices] IUserService userService,
        CancellationToken ct)
    {
```

- [ ] **Step 2: Replace `activeHumans` with snapshot-derived counts**

In `Index`'s body, replace:

```csharp
        var activeHumans = (await profileService.GetActiveApprovedUserIdsAsync(ct)).Count;
```

with:

```csharp
        var snapshot = userService.GetAllUserInfos();
        var totalUsers = snapshot.Count;
        var activeProfileUsers = snapshot.Count(u => u.Profile != null);
        var ticketHolders = snapshot.Count(u => u.HasTicket);
```

(Keep `profileService` injected — other actions on the controller use it. Just drop the one call.)

- [ ] **Step 3: Update the `new AdminDashboardViewModel(...)` constructor call**

Replace:

```csharp
        var vm = new AdminDashboardViewModel(
            GreetingFirstName: firstName,
            ActiveHumans: activeHumans,
            ShiftCoveragePercent: total > 0 ? (int)Math.Round(ratio * 100) : 0,
            // ...
```

with:

```csharp
        var vm = new AdminDashboardViewModel(
            GreetingFirstName: firstName,
            TotalUsers: totalUsers,
            ActiveProfileUsers: activeProfileUsers,
            TicketHolders: ticketHolders,
            ShiftCoveragePercent: total > 0 ? (int)Math.Round(ratio * 100) : 0,
            // ...
```

(Leave the rest of the constructor arguments unchanged.)

- [ ] **Step 4: Do not commit alone** — bundle with Task 10.

---

## Task 10: Update dashboard views

**Files:**
- Modify: `src/Humans.Web/Views/Shared/_DashboardStats.cshtml`
- Modify: `src/Humans.Web/Views/Admin/Index.cshtml`

- [ ] **Step 1: Replace `_DashboardStats.cshtml` body**

Open `src/Humans.Web/Views/Shared/_DashboardStats.cshtml`. Replace its entire contents with:

```cshtml
@model Humans.Web.Models.AdminDashboardViewModel

<div class="stats">
    <div class="stat">
        <div class="label">Total users</div>
        <div class="value">@Model.TotalUsers</div>
    </div>
    <div class="stat">
        <div class="label">Active (has profile)</div>
        <div class="value">@Model.ActiveProfileUsers</div>
    </div>
    <div class="stat">
        <div class="label">Ticket holders</div>
        <div class="value">@Model.TicketHolders</div>
    </div>
    <div class="stat">
        <div class="label">Shifts staffed</div>
        <div class="value">
            @if (Model.ShiftTotalOf.HasValue)
            {
                @Model.ShiftFilledOf <em>/ @Model.ShiftTotalOf</em>
            }
            else
            {
                <em>—</em>
            }
        </div>
        <div class="delta">@Model.ShiftCoveragePercent% of slots filled</div>
    </div>
    <div class="stat">
        <div class="label">Open feedback</div>
        <div class="value">@Model.OpenFeedback</div>
    </div>
</div>
```

- [ ] **Step 2: Update the page subtitle in `Admin/Index.cshtml`**

Open `src/Humans.Web/Views/Admin/Index.cshtml`. Replace line 10:

```cshtml
            @Model.ActiveHumans active humans · @Model.ShiftCoveragePercent% shift coverage · @Model.OpenFeedback open feedback
```

with:

```cshtml
            @Model.TotalUsers users · @Model.ActiveProfileUsers with profile · @Model.TicketHolders with ticket · @Model.ShiftCoveragePercent% shift coverage · @Model.OpenFeedback open feedback
```

- [ ] **Step 3: Build and run**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds with no errors related to `AdminDashboardViewModel` / `ActiveHumans`.

- [ ] **Step 4: Run tests**

Run: `dotnet test Humans.slnx -v quiet`
Expected: all tests pass. If any test asserts on `vm.ActiveHumans`, update to the new fields.

- [ ] **Step 5: Commit tasks 8–10 together**

```bash
git add src/Humans.Web/Models/AdminDashboardViewModel.cs \
        src/Humans.Web/Controllers/AdminController.cs \
        src/Humans.Web/Views/Shared/_DashboardStats.cshtml \
        src/Humans.Web/Views/Admin/Index.cshtml
git commit -m "feat(admin): replace Active Humans with Total/Active/TicketHolders cards"
```

- [ ] **Step 6: Push** (per memory rule, push every 3-5 tasks during long runs)

```bash
git push
```

---

## Task 11: `UsersDebugViewModel` + `UserDebugRow`

**Files:**
- Create: `src/Humans.Web/Models/UsersDebugViewModel.cs`

- [ ] **Step 1: Create the view model**

Create `src/Humans.Web/Models/UsersDebugViewModel.cs`:

```csharp
using Humans.Application;
using Humans.Domain.Enums;

namespace Humans.Web.Models;

public sealed record UsersDebugViewModel(
    IReadOnlyList<UserDebugRow> Rows,
    int TotalCount,
    int Page,
    int PageSize,
    string Sort,
    string Dir)
{
    public int TotalPages => PageSize > 0
        ? (int)Math.Ceiling((double)TotalCount / PageSize)
        : 0;

    public bool IsAsc => string.Equals(Dir, "asc", StringComparison.OrdinalIgnoreCase);
}

public sealed record UserDebugRow(
    Guid UserId,
    bool HasProfile,
    bool HasTicket,
    bool? MarketingOptedOut,
    string DisplayName,
    string BurnerName,
    string LegalName,
    bool? HasConsent)
{
    public static UserDebugRow From(UserInfo info) => new(
        UserId: info.Id,
        HasProfile: info.Profile is not null,
        HasTicket: info.HasTicket,
        MarketingOptedOut: info.MarketingOptedOut,
        DisplayName: info.DisplayName,
        BurnerName: info.Profile?.BurnerName ?? string.Empty,
        LegalName: info.Profile?.FullName ?? string.Empty,
        HasConsent: info.Profile is null
            ? null
            : info.Profile.ConsentCheckStatus == ConsentCheckStatus.Cleared);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 3: Commit** — bundle with Task 12.

---

## Task 12: `UsersAdminDebugController`

**Files:**
- Create: `src/Humans.Web/Controllers/UsersAdminDebugController.cs`
- Create: `tests/Humans.Web.Tests/Controllers/UsersAdminDebugControllerTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/Humans.Web.Tests/Controllers/UsersAdminDebugControllerTests.cs`:

```csharp
using FluentAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers;

public class UsersAdminDebugControllerTests
{
    private static UserInfo MakeUserInfo(
        Guid id, string displayName, bool hasProfile, bool hasTicket)
    {
        var participations = hasTicket
            ? new[]
            {
                new EventParticipation
                {
                    Id = Guid.NewGuid(),
                    UserId = id,
                    Year = 2026,
                    Status = ParticipationStatus.Ticketed,
                    Source = ParticipationSource.TicketSync,
                }
            }
            : Array.Empty<EventParticipation>();

        Profile? profile = hasProfile
            ? new Profile
            {
                Id = Guid.NewGuid(),
                UserId = id,
                BurnerName = "Burner",
                FirstName = "First",
                LastName = "Last",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            }
            : null;

        return UserInfo.Create(
            user: new User
            {
                Id = id,
                DisplayName = displayName,
                PreferredLanguage = "en",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                GoogleEmailStatus = GoogleEmailStatus.Unknown,
            },
            userEmails: Array.Empty<UserEmail>(),
            eventParticipations: participations,
            externalLogins: Array.Empty<(string, string)>(),
            profile: profile,
            contactFields: Array.Empty<ContactField>(),
            profileLanguages: Array.Empty<ProfileLanguage>(),
            volunteerHistory: Array.Empty<VolunteerHistoryEntry>(),
            communicationPreferences: Array.Empty<CommunicationPreference>());
    }

    [Fact]
    public void Index_returns_paged_rows_from_snapshot()
    {
        // Arrange — 60 users so default pageSize=50 leaves 10 on page 2.
        var users = Enumerable.Range(0, 60)
            .Select(i => MakeUserInfo(Guid.NewGuid(), $"User {i:D3}", hasProfile: i % 2 == 0, hasTicket: i % 3 == 0))
            .ToArray();

        var userService = Substitute.For<IUserService>();
        userService.GetAllUserInfos().Returns(users);

        var controller = new UsersAdminDebugController(userService);

        // Act
        var result = controller.Index(page: 1, pageSize: 50, sort: "displayName", dir: "asc") as ViewResult;

        // Assert
        result.Should().NotBeNull();
        var vm = result!.Model.Should().BeOfType<UsersDebugViewModel>().Subject;
        vm.TotalCount.Should().Be(60);
        vm.Rows.Should().HaveCount(50);
        vm.TotalPages.Should().Be(2);
        vm.Rows[0].DisplayName.Should().Be("User 000");
    }

    [Fact]
    public void Index_sort_displayName_descending_reverses_order()
    {
        var users = new[]
        {
            MakeUserInfo(Guid.NewGuid(), "Alice", hasProfile: true, hasTicket: false),
            MakeUserInfo(Guid.NewGuid(), "Bob",   hasProfile: true, hasTicket: false),
            MakeUserInfo(Guid.NewGuid(), "Carol", hasProfile: true, hasTicket: false),
        };
        var userService = Substitute.For<IUserService>();
        userService.GetAllUserInfos().Returns(users);

        var controller = new UsersAdminDebugController(userService);

        var result = controller.Index(page: 1, pageSize: 50, sort: "displayName", dir: "desc") as ViewResult;

        var vm = (UsersDebugViewModel)result!.Model!;
        vm.Rows.Select(r => r.DisplayName).Should().Equal("Carol", "Bob", "Alice");
    }

    [Fact]
    public void Index_clamps_pageSize_outside_10_to_200_range()
    {
        var users = Enumerable.Range(0, 5)
            .Select(i => MakeUserInfo(Guid.NewGuid(), $"User {i}", true, false))
            .ToArray();
        var userService = Substitute.For<IUserService>();
        userService.GetAllUserInfos().Returns(users);

        var controller = new UsersAdminDebugController(userService);

        var tooSmall = (UsersDebugViewModel)((ViewResult)controller.Index(1, 1, "displayName", "asc")).Model!;
        tooSmall.PageSize.Should().Be(10);

        var tooBig = (UsersDebugViewModel)((ViewResult)controller.Index(1, 9999, "displayName", "asc")).Model!;
        tooBig.PageSize.Should().Be(200);
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail (controller doesn't exist)**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UsersAdminDebugControllerTests"`
Expected: compile error — `UsersAdminDebugController` not found.

- [ ] **Step 3: Create the controller**

Create `src/Humans.Web/Controllers/UsersAdminDebugController.cs`:

```csharp
using Humans.Application;
using Humans.Application.Interfaces.Users;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// Diagnostic debug surface for the in-memory <see cref="UserInfo"/> cache.
/// Flat paginated/sortable table of every cached user — used to verify the
/// cache holds the expected data after imports and migrations. Every column
/// comes from <see cref="IUserService.GetAllUserInfos"/>; nothing on this
/// page makes a secondary query.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Users/Admin/Debug")]
public sealed class UsersAdminDebugController : Controller
{
    private const int MinPageSize = 10;
    private const int MaxPageSize = 200;
    private const int DefaultPageSize = 50;

    private readonly IUserService _userService;

    public UsersAdminDebugController(IUserService userService)
    {
        _userService = userService;
    }

    [HttpGet("")]
    public IActionResult Index(int page = 1, int pageSize = DefaultPageSize,
                               string sort = "displayName", string dir = "asc")
    {
        pageSize = Math.Clamp(pageSize, MinPageSize, MaxPageSize);
        if (page < 1) page = 1;

        var snapshot = _userService.GetAllUserInfos();
        var allRows = snapshot.Select(UserDebugRow.From).ToList();

        var sorted = ApplySort(allRows, sort, dir);
        var total = sorted.Count;
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return View(new UsersDebugViewModel(paged, total, page, pageSize, sort, dir));
    }

    private static List<UserDebugRow> ApplySort(List<UserDebugRow> rows, string sort, string dir)
    {
        var asc = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);

        // Null-first ascending semantics for tri-state booleans — null < false < true.
        static int NullableBool(bool? b) => b is null ? 0 : b.Value ? 2 : 1;

        IEnumerable<UserDebugRow> sorted = sort switch
        {
            "userId"      => rows.OrderBy(r => r.UserId),
            "hasProfile"  => rows.OrderBy(r => r.HasProfile),
            "hasTicket"   => rows.OrderBy(r => r.HasTicket),
            "marketing"   => rows.OrderBy(r => NullableBool(r.MarketingOptedOut)),
            "burnerName"  => rows.OrderBy(r => r.BurnerName, StringComparer.OrdinalIgnoreCase),
            "legalName"   => rows.OrderBy(r => r.LegalName, StringComparer.OrdinalIgnoreCase),
            "hasConsent"  => rows.OrderBy(r => NullableBool(r.HasConsent)),
            _             => rows.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase),
        };

        return asc ? sorted.ToList() : sorted.Reverse().ToList();
    }
}
```

- [ ] **Step 4: Run tests to confirm they pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UsersAdminDebugControllerTests"`
Expected: all three tests PASS.

- [ ] **Step 5: Commit tasks 11 + 12 together**

```bash
git add src/Humans.Web/Models/UsersDebugViewModel.cs \
        src/Humans.Web/Controllers/UsersAdminDebugController.cs \
        tests/Humans.Web.Tests/Controllers/UsersAdminDebugControllerTests.cs
git commit -m "feat(users-debug): controller + view model for /Users/Admin/Debug"
```

---

## Task 13: `Views/UsersAdminDebug/Index.cshtml`

**Files:**
- Create: `src/Humans.Web/Views/UsersAdminDebug/Index.cshtml`

- [ ] **Step 1: Look at an existing precedent for table styling**

Read `src/Humans.Web/Views/AdminDuplicateAccounts/Index.cshtml` (small, similar-shape admin table) to copy the table + pager layout. Adapt the column structure to the debug page's needs.

- [ ] **Step 2: Create the view**

Create `src/Humans.Web/Views/UsersAdminDebug/Index.cshtml`:

```cshtml
@model Humans.Web.Models.UsersDebugViewModel
@{
    ViewData["Title"] = "All users (debug)";

    string Toggle(string column)
    {
        var newDir = (Model.Sort == column && Model.IsAsc) ? "desc" : "asc";
        return Url.Action("Index", new { page = 1, pageSize = Model.PageSize, sort = column, dir = newDir })!;
    }

    string ArrowFor(string column) =>
        Model.Sort != column ? "" : Model.IsAsc ? " ↑" : " ↓";

    string Tri(bool? value) => value is null ? "—" : value.Value ? "Yes" : "No";
    string Bool(bool value) => value ? "✓" : "✗";
}

<div class="page-head">
    <h1 class="title">All users (debug)</h1>
    <p class="sub">
        @Model.TotalCount users · page @Model.Page of @Model.TotalPages · all fields read from the in-memory UserInfo cache
    </p>
</div>

<div class="card">
    <div class="card-body p-0">
        <div class="table-responsive">
            <table class="table table-sm table-hover mb-0">
                <thead>
                    <tr>
                        <th><a href="@Toggle("userId")">UserId@ArrowFor("userId")</a></th>
                        <th><a href="@Toggle("hasProfile")">HasProfile@ArrowFor("hasProfile")</a></th>
                        <th><a href="@Toggle("hasTicket")">HasTicket@ArrowFor("hasTicket")</a></th>
                        <th><a href="@Toggle("marketing")">Marketing@ArrowFor("marketing")</a></th>
                        <th><a href="@Toggle("displayName")">Display name@ArrowFor("displayName")</a></th>
                        <th><a href="@Toggle("burnerName")">Burner name@ArrowFor("burnerName")</a></th>
                        <th><a href="@Toggle("legalName")">Legal name@ArrowFor("legalName")</a></th>
                        <th><a href="@Toggle("hasConsent")">HasConsent@ArrowFor("hasConsent")</a></th>
                    </tr>
                </thead>
                <tbody>
                @foreach (var row in Model.Rows)
                {
                    <tr>
                        <td><code class="small">@row.UserId</code></td>
                        <td>@Bool(row.HasProfile)</td>
                        <td>@Bool(row.HasTicket)</td>
                        <td>@(row.MarketingOptedOut is null ? "—" : row.MarketingOptedOut.Value ? "No" : "Yes")</td>
                        <td>@row.DisplayName</td>
                        <td>@row.BurnerName</td>
                        <td>@row.LegalName</td>
                        <td>@Tri(row.HasConsent)</td>
                    </tr>
                }
                </tbody>
            </table>
        </div>
    </div>
</div>

@if (Model.TotalPages > 1)
{
    <nav class="mt-3" aria-label="pager">
        <ul class="pagination">
            @for (var p = 1; p <= Model.TotalPages; p++)
            {
                var href = Url.Action("Index", new { page = p, pageSize = Model.PageSize, sort = Model.Sort, dir = Model.Dir });
                <li class="page-item @(p == Model.Page ? "active" : "")">
                    <a class="page-link" href="@href">@p</a>
                </li>
            }
        </ul>
    </nav>
}

@* Marketing tri-state: "Yes" when subscribed (OptedOut == false), "No" when opted out, "—" when no row exists. *@
@* TODO: HasShift column — pending IShiftManager caching (see spec out-of-scope). *@
```

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 4: Commit** — bundle with Task 14.

---

## Task 14: AdminNavTree entry

**Files:**
- Modify: `src/Humans.Web/ViewComponents/AdminNavTree.cs`

- [ ] **Step 1: Add the nav item**

In `src/Humans.Web/ViewComponents/AdminNavTree.cs`, find the `Diagnostics` group (around line 75). Add a new entry inside it, after `"Cache stats"`:

```csharp
            new("All users (debug)", "UsersAdminDebug", "Index", null, null, "fa-solid fa-bug-slash", PolicyNames.AdminOnly),
```

The full group with the new entry (for context, the unchanged lines are in place):

```csharp
        new("Diagnostics", new AdminNavItem[]
        {
            new("Logs",            "Admin", "Logs",          null, null, "fa-solid fa-triangle-exclamation", PolicyNames.AdminOnly),
            new("DB stats",        "Admin", "DbStats",       null, null, "fa-solid fa-database",            PolicyNames.AdminOnly),
            new("Cache stats",     "Admin", "CacheStats",    null, null, "fa-solid fa-bolt",                PolicyNames.AdminOnly),
            new("All users (debug)", "UsersAdminDebug", "Index", null, null, "fa-solid fa-bug-slash", PolicyNames.AdminOnly),
            new("Configuration",   "Admin", "Configuration", null, null, "fa-solid fa-gear",                PolicyNames.AdminOnly),
            // ... rest unchanged
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 3: Run tests**

Run: `dotnet test Humans.slnx -v quiet`
Expected: all tests pass.

- [ ] **Step 4: Commit tasks 13 + 14**

```bash
git add src/Humans.Web/Views/UsersAdminDebug/Index.cshtml \
        src/Humans.Web/ViewComponents/AdminNavTree.cs
git commit -m "feat(users-debug): view + nav entry for /Users/Admin/Debug"
git push
```

---

## Task 15: `TicketDashboardViewModel` set-membership data

**Files:**
- Modify: `src/Humans.Web/Models/TicketViewModels.cs`

- [ ] **Step 1: Replace the coverage + participation fields with set-membership data**

In `src/Humans.Web/Models/TicketViewModels.cs`, in the `TicketDashboardViewModel` class:

Remove these properties:

```csharp
    // Volunteer ticket coverage
    public int TotalActiveVolunteers { get; set; }
    public int VolunteersWithTickets { get; set; }
    public decimal VolunteerCoveragePercent { get; set; }

    // Participation breakdown
    public int ParticipationNotAttending { get; set; }
    public int ParticipationNoTicket { get; set; }
    public int ParticipationHasTicket { get; set; }
```

Add this property:

```csharp
    // Set membership across UserInfo cache (Users, Profiles, Tickets).
    // Used by the Venn + UpSet diagrams. Each user is bucketed into one of
    // the four (HasProfile, HasTicket) combinations.
    public UserSetMembership? SetMembership { get; set; }
```

Below the `TicketDashboardViewModel` class, add the new record:

```csharp
/// <summary>
/// User-set bucketing for the Tickets dashboard Venn + UpSet diagrams.
/// Users is the universe (every UserInfo); Profiles and Tickets are subsets.
/// Each user falls into exactly one of the four buckets defined here.
/// </summary>
public sealed record UserSetMembership(
    int UsersOnly,              // !HasProfile && !HasTicket
    int UsersAndProfileOnly,    //  HasProfile && !HasTicket
    int UsersAndTicketOnly,     // !HasProfile &&  HasTicket
    int AllThree)               //  HasProfile &&  HasTicket
{
    public int TotalUsers => UsersOnly + UsersAndProfileOnly + UsersAndTicketOnly + AllThree;
    public int ProfilesCount => UsersAndProfileOnly + AllThree;
    public int TicketsCount => UsersAndTicketOnly + AllThree;
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: errors in `TicketController.cs` and `Views/Ticket/Index.cshtml` (next tasks).

- [ ] **Step 3: Do not commit alone** — bundle with Task 16 and 17.

---

## Task 16: `TicketController.Index` swap data source

**Files:**
- Modify: `src/Humans.Web/Controllers/TicketController.cs`

- [ ] **Step 1: Replace coverage assignments with snapshot-based bucketing**

In `src/Humans.Web/Controllers/TicketController.cs`, in `Index`:

Remove these lines from the model initializer (around lines 126-128):

```csharp
            TotalActiveVolunteers = stats.TotalActiveVolunteers,
            VolunteersWithTickets = stats.VolunteersWithTickets,
            VolunteerCoveragePercent = stats.VolunteerCoveragePercent,
```

Remove the entire `// Participation breakdown for donut chart` try/catch block (lines 131–154).

After the `var model = new TicketDashboardViewModel { ... };` declaration (and after the existing model assignments), add:

```csharp
        // Set membership across the UserInfo cache — drives the Venn + UpSet.
        var snapshot = _userService.GetAllUserInfos();
        var usersOnly = 0;
        var profileOnly = 0;
        var ticketOnly = 0;
        var both = 0;
        foreach (var u in snapshot)
        {
            var hasProfile = u.Profile is not null;
            var hasTicket = u.HasTicket;
            if (hasProfile && hasTicket) both++;
            else if (hasProfile) profileOnly++;
            else if (hasTicket) ticketOnly++;
            else usersOnly++;
        }
        model.SetMembership = new UserSetMembership(usersOnly, profileOnly, ticketOnly, both);
```

(Note: this also removes the `_shiftMgmt.GetActiveAsync()` call and the `_userService.GetAllParticipationsForYearAsync` call. Both are obsolete here — the snapshot's `HasTicket` accessor encodes the predicate.)

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds (view template still uses old fields — fixed in next task).

- [ ] **Step 3: Do not commit alone** — bundle with Task 17.

---

## Task 17: `/Tickets` view — remove old cards, add Venn + UpSet

**Files:**
- Modify: `src/Humans.Web/Views/Ticket/Index.cshtml`

- [ ] **Step 1: Remove the old "Volunteer Ticket Coverage" card**

In `src/Humans.Web/Views/Ticket/Index.cshtml`, delete lines 103–129 (the `<!-- Volunteer Ticket Coverage -->` block — from `@if (Model.TotalActiveVolunteers > 0)` through its closing `}`).

- [ ] **Step 2: Replace the "Participation Breakdown" card with the Venn + UpSet panel**

Delete the entire `<!-- Participation Breakdown -->` block (originally lines 131–168, indexes will shift after step 1). Insert in its place:

```cshtml
    <!-- User set membership — Venn (Profiles ∩ Tickets within Users) + UpSet -->
    @if (Model.SetMembership is { TotalUsers: > 0 } sm)
    {
        <div class="card mb-4">
            <div class="card-header">
                <i class="fa-solid fa-chart-pie"></i> User set membership
                <small class="text-muted">
                    @sm.TotalUsers total users · @sm.ProfilesCount with profile · @sm.TicketsCount with ticket
                </small>
            </div>
            <div class="card-body">
                <div class="row">
                    <div class="col-md-6">
                        <h6 class="text-center">Proportional Venn</h6>
                        <div id="userVenn"
                             data-users="@sm.TotalUsers"
                             data-profiles="@sm.ProfilesCount"
                             data-tickets="@sm.TicketsCount"
                             data-users-profiles="@sm.ProfilesCount"
                             data-users-tickets="@sm.TicketsCount"
                             data-profiles-tickets="@sm.AllThree"
                             data-all-three="@sm.AllThree"
                             style="max-height: 420px; width: 100%;"></div>
                    </div>
                    <div class="col-md-6">
                        <h6 class="text-center">UpSet plot</h6>
                        <div id="userUpset"
                             data-users-only="@sm.UsersOnly"
                             data-profile-only="@sm.UsersAndProfileOnly"
                             data-ticket-only="@sm.UsersAndTicketOnly"
                             data-all-three="@sm.AllThree"
                             style="max-height: 420px; width: 100%;"></div>
                    </div>
                </div>
                <p class="text-muted small mt-2 mb-0">
                    Users is the universe — every cached user is in it. Profiles ⊆ Users, Tickets ⊆ Users.
                </p>
            </div>
        </div>
        @* TODO: extend to Shifts set when IShiftManager caching lands (see 2026-05-14 spec). *@
    }
```

- [ ] **Step 3: Remove participation Chart.js block from the script section**

Find the `<script>` block at the bottom of the file (around line 372). Remove the `@if (participationTotal > 0)` block entirely (around lines 418–439 — the `<text> ... var partCtx ... </text>` block).

The outer `@if (Model.DailySales.Count > 0 || participationTotal > 0)` script-tag predicate becomes just `@if (Model.DailySales.Count > 0)` since `participationTotal` no longer exists. Update accordingly. Also remove the `@{ var participationTotal = ...; }` block higher in the file (around lines 132–134).

- [ ] **Step 4: Add Venn + UpSet rendering scripts at the bottom of the page**

After the existing `</script>` block, add:

```cshtml
@if (Model.SetMembership is not null && Model.SetMembership.TotalUsers > 0)
{
    <script src="https://cdn.jsdelivr.net/npm/@@upsetjs/venn.js@1.4.4/build/venn.min.js" crossorigin="anonymous"></script>
    <script src="https://cdn.jsdelivr.net/npm/@@upsetjs/bundle@1.13.1/build/upset.min.js" crossorigin="anonymous"></script>
    <script>
        document.addEventListener('DOMContentLoaded', function () {
            // ---- Venn: 3 sets (Users / Profiles / Tickets) ----
            var venn = document.getElementById('userVenn');
            if (venn && window.venn) {
                var d = venn.dataset;
                var sets = [
                    { sets: ['Users'], size: +d.users, label: 'Users (' + d.users + ')' },
                    { sets: ['Profiles'], size: +d.profiles, label: 'Profiles (' + d.profiles + ')' },
                    { sets: ['Tickets'], size: +d.tickets, label: 'Tickets (' + d.tickets + ')' },
                    { sets: ['Users', 'Profiles'], size: +d.usersProfiles },
                    { sets: ['Users', 'Tickets'], size: +d.usersTickets },
                    { sets: ['Profiles', 'Tickets'], size: +d.profilesTickets },
                    { sets: ['Users', 'Profiles', 'Tickets'], size: +d.allThree }
                ];
                var chart = window.venn.VennDiagram().width(400).height(360);
                d3.select('#userVenn').datum(sets).call(chart);
            }

            // ---- UpSet: same 3 sets, intersection bar chart ----
            var upset = document.getElementById('userUpset');
            if (upset && window.UpSetJS) {
                var u = upset.dataset;
                var usersOnly = +u.usersOnly;
                var profileOnly = +u.profileOnly;
                var ticketOnly = +u.ticketOnly;
                var allThree = +u.allThree;
                var elems = [];
                for (var i = 0; i < usersOnly; i++)    elems.push({ name: 'u' + i, sets: ['Users'] });
                for (var i = 0; i < profileOnly; i++)  elems.push({ name: 'p' + i, sets: ['Users', 'Profiles'] });
                for (var i = 0; i < ticketOnly; i++)   elems.push({ name: 't' + i, sets: ['Users', 'Tickets'] });
                for (var i = 0; i < allThree; i++)     elems.push({ name: 'a' + i, sets: ['Users', 'Profiles', 'Tickets'] });
                var sets = window.UpSetJS.extractSets(elems);
                var combinations = window.UpSetJS.generateCombinations(sets);
                window.UpSetJS.render(upset, {
                    sets: sets,
                    combinations: combinations,
                    width: 400,
                    height: 360
                });
            }
        });
    </script>
}
```

Note the `@@upsetjs` — Razor `@` escaping: `@@` emits a literal `@`.

- [ ] **Step 5: Verify the venn.js + upset.js library URLs**

Run: `curl -I https://cdn.jsdelivr.net/npm/@upsetjs/venn.js@1.4.4/build/venn.min.js`
Expected: HTTP 200.

Run: `curl -I https://cdn.jsdelivr.net/npm/@upsetjs/bundle@1.13.1/build/upset.min.js`
Expected: HTTP 200.

If either URL returns 404, look up the correct latest version on jsdelivr (the package names are correct; only the version may have changed). Update the URLs in the view accordingly.

- [ ] **Step 6: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 7: Run tests**

Run: `dotnet test Humans.slnx -v quiet`
Expected: all tests pass.

- [ ] **Step 8: Commit tasks 15–17 together**

```bash
git add src/Humans.Web/Models/TicketViewModels.cs \
        src/Humans.Web/Controllers/TicketController.cs \
        src/Humans.Web/Views/Ticket/Index.cshtml
git commit -m "feat(tickets): replace coverage card + donut with Venn + UpSet"
```

---

## Task 18: Smoke + final verification

**Files:** (none modified, manual verification only)

- [ ] **Step 1: Full build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds, zero warnings related to this work.

- [ ] **Step 2: Full test suite**

Run: `dotnet test Humans.slnx -v quiet`
Expected: every test passes.

- [ ] **Step 3: Launch the app and visually verify**

Run: `dotnet run --project src/Humans.Web`

Open `https://localhost:<port>/Admin` (admin user). Verify:
- Three new stat cards: Total users, Active (has profile), Ticket holders. No "Active humans 979" card.
- Existing Shifts staffed, Open feedback, tier app totals, language chart all still present.

Open `https://localhost:<port>/Users/Admin/Debug`. Verify:
- Table renders all cached users.
- Pager works, sort headers toggle correctly, Marketing tri-state displays "Yes" / "No" / "—".
- Page subtitle shows the correct total count.

Open `https://localhost:<port>/Tickets`. Verify:
- "Volunteer Ticket Coverage" card is GONE.
- "Participation Breakdown" donut is GONE.
- A new "User set membership" card shows a 3-set Venn (left) and UpSet (right), both bounded in height.
- Daily sales chart still renders.

Open the admin sidebar — "All users (debug)" link is under the Diagnostics group, with the bug-slash icon.

If anything looks wrong, fix and re-verify. UI feature correctness can't be guaranteed by tests alone (per CLAUDE.md).

- [ ] **Step 4: Final push**

```bash
git push
```

- [ ] **Step 5: Open the PR**

Use the GitHub CLI to open the PR against peter's fork:

```bash
gh pr create --title "feat(admin/users/tickets): UserInfo-driven stats, debug page, Venn" \
  --body "$(cat <<'EOF'
## Summary
- Replaces `/Admin` "Active humans 979" with three UserInfo-snapshot cards: Total users / Active (has profile) / Ticket holders.
- Adds `/Users/Admin/Debug` flat paginated/sortable table — every column from the cached `UserInfo`, no secondary queries.
- Replaces `/Tickets` coverage card + unbounded donut with a 3-set Venn (Users / Profiles / Tickets) + UpSet plot, both height-bounded.
- Extends `UserInfo` with `CommunicationPreferences` so Marketing opt-in lives on the cached god-object.

Spec: `docs/superpowers/specs/2026-05-14-userinfo-debug-and-venn-design.md`

## Test plan
- [ ] Build is clean: `dotnet build Humans.slnx -v quiet`
- [ ] Tests pass: `dotnet test Humans.slnx -v quiet`
- [ ] `/Admin` dashboard shows three new cards, no "Active humans"
- [ ] `/Users/Admin/Debug` lists all cached users with paging + sorting
- [ ] `/Tickets` shows Venn + UpSet, old cards gone, donut gone
- [ ] Admin nav has "All users (debug)" under Diagnostics

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Self-review checklist (after plan written)

- [x] **Spec coverage** — every spec section maps to a task:
  - §1 UserInfo extension → Task 1 + Task 2
  - §2 Cache wiring → Task 4 + Task 5 + Task 6
  - §3 Invalidation → Task 7
  - §4 `/Admin` dashboard → Task 8 + Task 9 + Task 10
  - §5 `/Users/Admin/Debug` → Task 11 + Task 12 + Task 13 + Task 14
  - §6 `/Tickets` → Task 15 + Task 16 + Task 17
  - Repository support → Task 3
  - Final smoke → Task 18
- [x] **Placeholder scan** — no "TBD" / "later" / "appropriate" / unreferenced types. Each step is concrete code or a concrete command.
- [x] **Type consistency** — `UserInfo.HasTicket`, `UserInfo.MarketingOptedOut`, `IUserService.GetAllUserInfos()`, `UserSetMembership`, `UsersDebugViewModel`, `UserDebugRow` — same names used consistently across the tasks that consume them.
