# /Profile/Admin/EmailProblems Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `/Profile/Admin/EmailProblems` — a Profile-section admin page that surfaces every UserEmail invariant violation (8 cases) and offers a consolidating merge action backed by a reusable `FoldAsync` kernel inside `AccountMergeService`.

**Architecture:** New `IEmailProblemsService` consumes only existing section services (`IProfileService`, `IUserEmailService`, `IUserService`) — never any repository directly. Detection sources from the `FullProfile` cache snapshot (extended to retain per-row UserEmail flags). Merge action calls a new `AccountMergeService.AdminMergeAsync` entry point, which shares a private `FoldAsync` kernel with the existing user-initiated `AcceptAsync`.

**Tech Stack:** ASP.NET Core MVC, EF Core, NodaTime, xUnit, Clean Architecture (4 layers).

**Spec:** `docs/superpowers/specs/2026-05-05-email-problems-page-design.md`

**Working directory:** `H:/source/Humans/.worktrees/issue-660-email-problems` (branch `issue-660-email-problems`).

**Build commands** (run from worktree root, never `cd <dir> && cmd`):
- `dotnet build Humans.slnx -v quiet`
- `dotnet test Humans.slnx -v quiet`
- Single test: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~<TestName>"`

---

## Phase 1 — Foundation: extend FullProfile + section service surfaces

### Task 1: Add `UserEmailSnapshot` record and extend `FullProfile`

**Files:**
- Modify: `src/Humans.Application/FullProfile.cs`

- [ ] **Step 1: Add `UserEmailSnapshot` record at the top of `FullProfile.cs`**

```csharp
namespace Humans.Application;

/// <summary>
/// Compact projection of a <see cref="Domain.Entities.UserEmail"/> row carried
/// inside <see cref="FullProfile"/> so consumers can inspect per-row flags
/// (IsPrimary / IsGoogle / IsVerified) without going back to the repository.
/// </summary>
public sealed record UserEmailSnapshot(
    Guid Id,
    string Email,
    bool IsVerified,
    bool IsPrimary,
    bool IsGoogle);
```

- [ ] **Step 2: Add `UserEmails` to the `FullProfile` record params (after `GoogleEmail`, before `State`)**

The new param is `IReadOnlyList<UserEmailSnapshot>? UserEmails = null`. Default null for backward compat with existing `Create` overloads that don't have email rows.

```csharp
public record FullProfile(
    // ... existing params ...
    string? GoogleEmail = null,
    IReadOnlyList<UserEmailSnapshot>? UserEmails = null,
    ProfileState? State = null)
{
```

- [ ] **Step 3: Add a non-null projection helper near `VerifiedEmails`**

```csharp
/// <summary>
/// Defensive non-null projection of <see cref="UserEmails"/>.
/// </summary>
public IReadOnlyList<UserEmailSnapshot> AllUserEmails =>
    UserEmails ?? Array.Empty<UserEmailSnapshot>();
```

- [ ] **Step 4: Build to confirm record signature change compiles**

Run: `dotnet build Humans.slnx -v quiet`
Expected: PASS (no callers yet rely on the new param positionally — it's optional).

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/FullProfile.cs
git commit -m "feat(profile): carry per-row UserEmail snapshot on FullProfile"
```

---

### Task 2: Populate `FullProfile.UserEmails` inside the rich `Create` overload

**Files:**
- Modify: `src/Humans.Application/FullProfile.cs`

- [ ] **Step 1: Inside `Create(profile, user, volunteerHistory, userEmails)`, build the snapshot list before constructing the FullProfile**

```csharp
var snapshots = userEmails
    .Select(e => new UserEmailSnapshot(e.Id, e.Email, e.IsVerified, e.IsPrimary, e.IsGoogle))
    .ToList();
```

- [ ] **Step 2: Pass `UserEmails: snapshots` into the `new FullProfile(...)` construction**

Add the named arg between `GoogleEmail: google,` and `State: profile.State)`.

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/FullProfile.cs
git commit -m "feat(profile): populate UserEmails snapshot in FullProfile.Create"
```

---

### Task 3: Add `IProfileService.GetFullProfileSnapshotAsync`

**Files:**
- Modify: `src/Humans.Application/Interfaces/Profiles/IProfileService.cs`
- Modify: `src/Humans.Application/Services/Profile/ProfileService.cs` (inner)
- Modify: `src/Humans.Infrastructure/Services/Profiles/CachingProfileService.cs` (decorator)

- [ ] **Step 1: Add the interface method**

In `IProfileService.cs`, near other snapshot-style methods:

```csharp
/// <summary>
/// Returns a snapshot of every <see cref="FullProfile"/> currently materialized.
/// Used by admin scans (EmailProblems) that need to enumerate the full set
/// of users with their per-row UserEmail flags. Decorator returns the cache
/// dict's Values; inner impl rebuilds from repositories.
/// </summary>
Task<IReadOnlyList<FullProfile>> GetFullProfileSnapshotAsync(CancellationToken ct = default);
```

- [ ] **Step 2: Implement on inner `ProfileService` by reusing the warmup-style load**

In `Humans.Application/Services/Profile/ProfileService.cs`, add:

```csharp
public async Task<IReadOnlyList<FullProfile>> GetFullProfileSnapshotAsync(CancellationToken ct = default)
{
    var profiles = await _profileRepository.GetAllAsync(ct);
    var userIds = profiles.Select(p => p.UserId).ToList();
    var users = await _userService.GetByIdsAsync(userIds, ct);
    var allUserEmails = await _userEmailRepository.GetAllAsync(ct);
    var emailsByUser = allUserEmails.GroupBy(e => e.UserId)
        .ToDictionary(g => g.Key, g => (IReadOnlyList<UserEmail>)g.ToList());

    var result = new List<FullProfile>(profiles.Count);
    foreach (var profile in profiles)
    {
        if (!users.TryGetValue(profile.UserId, out var user)) continue;
        var emails = emailsByUser.GetValueOrDefault(profile.UserId, Array.Empty<UserEmail>());
        result.Add(FullProfile.Create(profile, user, profile.VolunteerHistory.ToList(), emails));
    }
    return result;
}
```

(Confirm `_userService` and `_userEmailRepository` injections already exist by reading the constructor — both are used elsewhere in this file.)

- [ ] **Step 3: Implement on decorator (`CachingProfileService`) — return cache snapshot**

```csharp
public Task<IReadOnlyList<FullProfile>> GetFullProfileSnapshotAsync(CancellationToken ct = default) =>
    Task.FromResult<IReadOnlyList<FullProfile>>(_byUserId.Values.ToList());
```

- [ ] **Step 4: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Interfaces/Profiles/IProfileService.cs \
        src/Humans.Application/Services/Profile/ProfileService.cs \
        src/Humans.Infrastructure/Services/Profiles/CachingProfileService.cs
git commit -m "feat(profile): expose GetFullProfileSnapshotAsync on IProfileService"
```

---

### Task 4: Add `IUserEmailService.GetOrphanUserEmailsAsync`

**Files:**
- Modify: `src/Humans.Application/Interfaces/Profiles/IUserEmailService.cs`
- Modify: `src/Humans.Application/Services/Profile/UserEmailService.cs`

- [ ] **Step 1: Add interface method**

```csharp
/// <summary>
/// Returns UserEmail rows whose UserId points to a non-existent or tombstoned
/// User (User row absent OR <c>MergedToUserId</c> set). Used by the EmailProblems
/// admin scan. At ~500 users, full-table scan is trivial.
/// </summary>
Task<IReadOnlyList<UserEmail>> GetOrphanUserEmailsAsync(CancellationToken ct = default);
```

- [ ] **Step 2: Implement on `UserEmailService` using existing `IUserService.GetAllUsersAsync` + repo `GetAllAsync`**

```csharp
public async Task<IReadOnlyList<UserEmail>> GetOrphanUserEmailsAsync(CancellationToken ct = default)
{
    var allEmails = await _repository.GetAllAsync(ct);
    var allUsers = await _userService.GetAllUsersAsync(ct);
    var liveUserIds = allUsers
        .Where(u => u.MergedToUserId is null)
        .Select(u => u.Id)
        .ToHashSet();

    return allEmails
        .Where(e => !liveUserIds.Contains(e.UserId))
        .ToList();
}
```

(Confirm `_userService` is already injected in `UserEmailService`. If not, add the injection — `IUserService` is already used by sibling services in this file.)

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Interfaces/Profiles/IUserEmailService.cs \
        src/Humans.Application/Services/Profile/UserEmailService.cs
git commit -m "feat(profile): add GetOrphanUserEmailsAsync to IUserEmailService"
```

---

### Task 5: Add `IUserService.GetUsersWithLoginsButNoEmailsAsync` (with repo method)

**Files:**
- Modify: `src/Humans.Application/Interfaces/Repositories/IUserRepository.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Users/UserRepository.cs`
- Modify: `src/Humans.Application/Interfaces/Users/IUserService.cs`
- Modify: `src/Humans.Application/Services/Users/UserService.cs`

- [ ] **Step 1: Add repo method to `IUserRepository`**

```csharp
/// <summary>
/// Returns userIds of users that have at least one row in
/// <c>AspNetUserLogins</c> but zero rows in <c>user_emails</c>. Used by the
/// EmailProblems admin scan to surface ghost auth artifacts (case 8).
/// </summary>
Task<IReadOnlyList<Guid>> GetUsersWithLoginsButNoEmailsAsync(CancellationToken ct = default);
```

- [ ] **Step 2: Implement on `UserRepository`**

Open `src/Humans.Infrastructure/Repositories/Users/UserRepository.cs`. Add (matching the section's `IDbContextFactory` + AsNoTracking idiom used by other read methods):

```csharp
public async Task<IReadOnlyList<Guid>> GetUsersWithLoginsButNoEmailsAsync(CancellationToken ct = default)
{
    using var ctx = await _contextFactory.CreateDbContextAsync(ct);

    // Distinct UserIds present in AspNetUserLogins
    var loginUserIds = await ctx.UserLogins
        .AsNoTracking()
        .Select(l => l.UserId)
        .Distinct()
        .ToListAsync(ct);

    if (loginUserIds.Count == 0) return Array.Empty<Guid>();

    // UserIds that DO have a user_emails row
    var withEmail = await ctx.UserEmails
        .AsNoTracking()
        .Where(e => loginUserIds.Contains(e.UserId))
        .Select(e => e.UserId)
        .Distinct()
        .ToListAsync(ct);

    var withEmailSet = withEmail.ToHashSet();
    return loginUserIds.Where(id => !withEmailSet.Contains(id)).ToList();
}
```

- [ ] **Step 3: Add interface method to `IUserService`**

```csharp
/// <summary>
/// Returns userIds of users that have AspNetUserLogins rows but zero
/// UserEmail rows. Used by EmailProblems admin scan.
/// </summary>
Task<IReadOnlyList<Guid>> GetUsersWithLoginsButNoEmailsAsync(CancellationToken ct = default);
```

- [ ] **Step 4: Implement on `UserService` (pass-through to repo)**

```csharp
public Task<IReadOnlyList<Guid>> GetUsersWithLoginsButNoEmailsAsync(CancellationToken ct = default) =>
    _repo.GetUsersWithLoginsButNoEmailsAsync(ct);
```

- [ ] **Step 5: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Interfaces/Repositories/IUserRepository.cs \
        src/Humans.Infrastructure/Repositories/Users/UserRepository.cs \
        src/Humans.Application/Interfaces/Users/IUserService.cs \
        src/Humans.Application/Services/Users/UserService.cs
git commit -m "feat(users): add GetUsersWithLoginsButNoEmailsAsync"
```

---

### Task 6: Add new `AuditAction` enum values

**Files:**
- Modify: `src/Humans.Domain/Enums/AuditAction.cs`

- [ ] **Step 1: Append two new values to the enum**

```csharp
OrphanUserEmailDeleted,
GhostExternalLoginsDeleted,
```

(Append at the end of the enum to preserve string-conversion stability for existing rows.)

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Domain/Enums/AuditAction.cs
git commit -m "feat(audit): add OrphanUserEmailDeleted, GhostExternalLoginsDeleted actions"
```

- [ ] **Step 4: Push the foundation phase**

```bash
git push
```

---

## Phase 2 — Detection service (TDD)

### Task 7: Create `EmailProblemsReport`, `IEmailProblemsService`, empty service skeleton

**Files:**
- Create: `src/Humans.Application/DTOs/EmailProblems/EmailProblemKind.cs`
- Create: `src/Humans.Application/DTOs/EmailProblems/EmailProblem.cs`
- Create: `src/Humans.Application/DTOs/EmailProblems/EmailProblemsReport.cs`
- Create: `src/Humans.Application/Interfaces/Profiles/IEmailProblemsService.cs`
- Create: `src/Humans.Application/Services/Profile/EmailProblemsService.cs`
- Modify: `src/Humans.Web/Extensions/Sections/ProfileSectionExtensions.cs`

- [ ] **Step 1: Create `EmailProblemKind.cs`**

```csharp
namespace Humans.Application.DTOs.EmailProblems;

public enum EmailProblemKind
{
    MultipleIsPrimary = 1,
    MultipleIsGoogle = 2,
    ZeroIsPrimary = 3,
    ZeroIsGoogle = 4,
    SharedAcrossUsers = 5,
    Unverified = 6,
    OrphanUserEmail = 7,
    GhostExternalLogins = 8
}
```

- [ ] **Step 2: Create `EmailProblem.cs`**

```csharp
using Humans.Application.DTOs.EmailProblems;

namespace Humans.Application.DTOs.EmailProblems;

/// <summary>
/// One detected EmailProblem entry. Some kinds are scoped to a single user,
/// some to a pair (case 5), some to a single email row (case 7), some to a
/// single user with no rows (case 8).
/// </summary>
public sealed record EmailProblem(
    EmailProblemKind Kind,
    Guid? UserId,
    Guid? OtherUserId,
    Guid? UserEmailId,
    string? Email,
    string? Detail);
```

- [ ] **Step 3: Create `EmailProblemsReport.cs`**

```csharp
using NodaTime;

namespace Humans.Application.DTOs.EmailProblems;

public sealed record EmailProblemsReport(
    Instant ScannedAt,
    IReadOnlyList<EmailProblem> Problems);
```

- [ ] **Step 4: Create `IEmailProblemsService.cs`**

```csharp
using Humans.Application.DTOs.EmailProblems;

namespace Humans.Application.Interfaces.Profiles;

/// <summary>
/// Scans every UserEmail invariant violation surface for the
/// <c>/Profile/Admin/EmailProblems</c> page. Consumes only existing section
/// services — never any <c>I*Repository</c> or <c>DbContext</c>.
/// </summary>
public interface IEmailProblemsService
{
    Task<EmailProblemsReport> ScanAsync(CancellationToken ct = default);
}
```

- [ ] **Step 5: Create empty `EmailProblemsService.cs`**

```csharp
using Humans.Application.DTOs.EmailProblems;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using NodaTime;

namespace Humans.Application.Services.Profile;

public sealed class EmailProblemsService : IEmailProblemsService
{
    private readonly IProfileService _profileService;
    private readonly IUserEmailService _userEmailService;
    private readonly IUserService _userService;
    private readonly IClock _clock;

    public EmailProblemsService(
        IProfileService profileService,
        IUserEmailService userEmailService,
        IUserService userService,
        IClock clock)
    {
        _profileService = profileService;
        _userEmailService = userEmailService;
        _userService = userService;
        _clock = clock;
    }

    public async Task<EmailProblemsReport> ScanAsync(CancellationToken ct = default)
    {
        var problems = new List<EmailProblem>();
        // Tasks 8–13 fill this in.
        return new EmailProblemsReport(_clock.GetCurrentInstant(), problems);
    }
}
```

- [ ] **Step 6: Register in DI**

In `ProfileSectionExtensions.cs`, after the `IDuplicateAccountService` registration, add:

```csharp
services.AddScoped<IEmailProblemsService, EmailProblemsService>();
```

- [ ] **Step 7: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Application/DTOs/EmailProblems/ \
        src/Humans.Application/Interfaces/Profiles/IEmailProblemsService.cs \
        src/Humans.Application/Services/Profile/EmailProblemsService.cs \
        src/Humans.Web/Extensions/Sections/ProfileSectionExtensions.cs
git commit -m "feat(emailproblems): scaffold IEmailProblemsService + DTOs"
```

---

### Task 8: Detect cases 1 & 2 — multiple `IsPrimary` / multiple `IsGoogle` per user (TDD)

**Files:**
- Create: `tests/Humans.Application.Tests/Services/Profile/EmailProblemsServiceTests.cs`
- Modify: `src/Humans.Application/Services/Profile/EmailProblemsService.cs`

- [ ] **Step 1: Write the failing test file**

```csharp
using FluentAssertions;
using Humans.Application;
using Humans.Application.DTOs.EmailProblems;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profile;
using Humans.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodaTime.Testing;
using Xunit;

namespace Humans.Application.Tests.Services.Profile;

public class EmailProblemsServiceTests
{
    private readonly Mock<IProfileService> _profileService = new();
    private readonly Mock<IUserEmailService> _userEmailService = new();
    private readonly Mock<IUserService> _userService = new();
    private readonly FakeClock _clock = new(NodaTime.Instant.FromUtc(2026, 5, 5, 12, 0));

    private EmailProblemsService Sut => new(
        _profileService.Object, _userEmailService.Object, _userService.Object, _clock);

    private static FullProfile MakeProfile(Guid userId, params UserEmailSnapshot[] emails) =>
        new FullProfile(
            UserId: userId, DisplayName: "Test User", ProfilePictureUrl: null,
            HasCustomPicture: false, ProfileId: Guid.NewGuid(), UpdatedAtTicks: 0,
            BurnerName: "Test", Bio: null, Pronouns: null, ContributionInterests: null,
            City: null, CountryCode: null, Latitude: null, Longitude: null,
            BirthdayDay: null, BirthdayMonth: null,
            IsApproved: true, IsSuspended: false,
            CVEntries: Array.Empty<CVEntry>(),
            UserEmails: emails);

    private void SetProfiles(params FullProfile[] profiles) =>
        _profileService.Setup(s => s.GetFullProfileSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profiles);

    private void SetOrphans(params UserEmail[] orphans) =>
        _userEmailService.Setup(s => s.GetOrphanUserEmailsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(orphans);

    private void SetGhosts(params Guid[] ghostUserIds) =>
        _userService.Setup(s => s.GetUsersWithLoginsButNoEmailsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ghostUserIds);

    [Fact]
    public async Task EmptySnapshot_ReturnsEmptyReport()
    {
        SetProfiles();
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectsMultipleIsPrimary()
    {
        var userId = Guid.NewGuid();
        SetProfiles(MakeProfile(userId,
            new UserEmailSnapshot(Guid.NewGuid(), "a@x.com", true, true, false),
            new UserEmailSnapshot(Guid.NewGuid(), "b@x.com", true, true, false)));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.MultipleIsPrimary && p.UserId == userId);
    }

    [Fact]
    public async Task DetectsMultipleIsGoogle()
    {
        var userId = Guid.NewGuid();
        SetProfiles(MakeProfile(userId,
            new UserEmailSnapshot(Guid.NewGuid(), "a@x.com", true, false, true),
            new UserEmailSnapshot(Guid.NewGuid(), "b@x.com", true, false, true)));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.MultipleIsGoogle && p.UserId == userId);
    }
}
```

- [ ] **Step 2: Run tests, expect failures**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~EmailProblemsServiceTests"`
Expected: FAIL — `EmptySnapshot_ReturnsEmptyReport` passes (empty in, empty out), but the two detection tests fail because no detection is implemented.

- [ ] **Step 3: Add detection logic in `ScanAsync`**

Replace the body of `ScanAsync` in `EmailProblemsService.cs`:

```csharp
public async Task<EmailProblemsReport> ScanAsync(CancellationToken ct = default)
{
    var problems = new List<EmailProblem>();

    var profiles = await _profileService.GetFullProfileSnapshotAsync(ct);

    foreach (var p in profiles)
    {
        var emails = p.AllUserEmails;

        if (emails.Count(e => e.IsPrimary) > 1)
            problems.Add(new EmailProblem(
                EmailProblemKind.MultipleIsPrimary, p.UserId, null, null, null, null));

        if (emails.Count(e => e.IsGoogle) > 1)
            problems.Add(new EmailProblem(
                EmailProblemKind.MultipleIsGoogle, p.UserId, null, null, null, null));
    }

    return new EmailProblemsReport(_clock.GetCurrentInstant(), problems);
}
```

- [ ] **Step 4: Run tests, expect pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~EmailProblemsServiceTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add tests/Humans.Application.Tests/Services/Profile/EmailProblemsServiceTests.cs \
        src/Humans.Application/Services/Profile/EmailProblemsService.cs
git commit -m "feat(emailproblems): detect multiple IsPrimary/IsGoogle (cases 1, 2)"
```

---

### Task 9: Detect cases 3 & 4 — zero `IsPrimary` / zero `IsGoogle` (TDD)

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/Profile/EmailProblemsServiceTests.cs`
- Modify: `src/Humans.Application/Services/Profile/EmailProblemsService.cs`

- [ ] **Step 1: Add failing tests**

```csharp
[Fact]
public async Task DetectsZeroIsPrimary_WhenUserHasVerifiedEmails()
{
    var userId = Guid.NewGuid();
    SetProfiles(MakeProfile(userId,
        new UserEmailSnapshot(Guid.NewGuid(), "a@x.com", true, false, false),
        new UserEmailSnapshot(Guid.NewGuid(), "b@x.com", true, false, false)));
    SetOrphans();
    SetGhosts();

    var report = await Sut.ScanAsync();

    report.Problems.Should().ContainSingle(p =>
        p.Kind == EmailProblemKind.ZeroIsPrimary && p.UserId == userId);
}

[Fact]
public async Task DoesNotFlagZeroIsPrimary_WhenUserHasNoVerifiedEmails()
{
    var userId = Guid.NewGuid();
    SetProfiles(MakeProfile(userId,
        new UserEmailSnapshot(Guid.NewGuid(), "a@x.com", false, false, false)));
    SetOrphans();
    SetGhosts();

    var report = await Sut.ScanAsync();

    report.Problems.Should().NotContain(p => p.Kind == EmailProblemKind.ZeroIsPrimary);
}

[Fact]
public async Task DetectsZeroIsGoogle()
{
    var userId = Guid.NewGuid();
    SetProfiles(MakeProfile(userId,
        new UserEmailSnapshot(Guid.NewGuid(), "a@x.com", true, true, false)));
    SetOrphans();
    SetGhosts();

    var report = await Sut.ScanAsync();

    report.Problems.Should().ContainSingle(p =>
        p.Kind == EmailProblemKind.ZeroIsGoogle && p.UserId == userId);
}
```

- [ ] **Step 2: Run, expect 2 of 3 to fail (the "DoesNotFlag" passes vacuously)**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~EmailProblemsServiceTests"`
Expected: FAIL.

- [ ] **Step 3: Add detection logic in `ScanAsync` (inside the per-profile loop)**

```csharp
if (emails.Any(e => e.IsVerified) && !emails.Any(e => e.IsPrimary))
    problems.Add(new EmailProblem(
        EmailProblemKind.ZeroIsPrimary, p.UserId, null, null, null, null));

if (!emails.Any(e => e.IsGoogle))
    problems.Add(new EmailProblem(
        EmailProblemKind.ZeroIsGoogle, p.UserId, null, null, null, null));
```

- [ ] **Step 4: Run tests, expect pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~EmailProblemsServiceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/Humans.Application.Tests/Services/Profile/EmailProblemsServiceTests.cs \
        src/Humans.Application/Services/Profile/EmailProblemsService.cs
git commit -m "feat(emailproblems): detect zero IsPrimary/IsGoogle (cases 3, 4)"
```

---

### Task 10: Detect case 6 — any unverified UserEmail (TDD)

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/Profile/EmailProblemsServiceTests.cs`
- Modify: `src/Humans.Application/Services/Profile/EmailProblemsService.cs`

- [ ] **Step 1: Add failing test**

```csharp
[Fact]
public async Task DetectsUnverifiedEmail_RegardlessOfFlags()
{
    var userId = Guid.NewGuid();
    var emailId = Guid.NewGuid();
    SetProfiles(MakeProfile(userId,
        new UserEmailSnapshot(emailId, "a@x.com", false, false, false)));
    SetOrphans();
    SetGhosts();

    var report = await Sut.ScanAsync();

    report.Problems.Should().ContainSingle(p =>
        p.Kind == EmailProblemKind.Unverified
        && p.UserId == userId
        && p.UserEmailId == emailId
        && p.Email == "a@x.com");
}
```

- [ ] **Step 2: Run, expect fail**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~EmailProblemsServiceTests"`
Expected: FAIL.

- [ ] **Step 3: Add detection inside the per-profile loop in `ScanAsync`**

```csharp
foreach (var unverified in emails.Where(e => !e.IsVerified))
{
    problems.Add(new EmailProblem(
        EmailProblemKind.Unverified, p.UserId, null,
        unverified.Id, unverified.Email, null));
}
```

- [ ] **Step 4: Run, expect pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~EmailProblemsServiceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/Humans.Application.Tests/Services/Profile/EmailProblemsServiceTests.cs \
        src/Humans.Application/Services/Profile/EmailProblemsService.cs
git commit -m "feat(emailproblems): detect unverified UserEmail rows (case 6)"
```

---

### Task 11: Detect case 5 — two users sharing an email (with normalization) (TDD)

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/Profile/EmailProblemsServiceTests.cs`
- Modify: `src/Humans.Application/Services/Profile/EmailProblemsService.cs`

- [ ] **Step 1: Add failing tests**

```csharp
[Fact]
public async Task DetectsRawEmailCollisionAcrossUsers()
{
    var u1 = Guid.NewGuid();
    var u2 = Guid.NewGuid();
    SetProfiles(
        MakeProfile(u1, new UserEmailSnapshot(Guid.NewGuid(), "joe@x.com", true, true, false)),
        MakeProfile(u2, new UserEmailSnapshot(Guid.NewGuid(), "joe@x.com", true, true, false)));
    SetOrphans();
    SetGhosts();

    var report = await Sut.ScanAsync();

    report.Problems.Should().ContainSingle(p => p.Kind == EmailProblemKind.SharedAcrossUsers)
        .Which.Should().Match<EmailProblem>(p =>
            p.Email == "joe@x.com"
            && (p.UserId == u1 || p.UserId == u2)
            && (p.OtherUserId == u1 || p.OtherUserId == u2)
            && p.UserId != p.OtherUserId);
}

[Fact]
public async Task DetectsNormalizationEquivalentCollision()
{
    var u1 = Guid.NewGuid();
    var u2 = Guid.NewGuid();
    SetProfiles(
        MakeProfile(u1, new UserEmailSnapshot(Guid.NewGuid(), "joe@gmail.com", true, true, false)),
        MakeProfile(u2, new UserEmailSnapshot(Guid.NewGuid(), "j.oe@googlemail.com", true, true, false)));
    SetOrphans();
    SetGhosts();

    var report = await Sut.ScanAsync();

    report.Problems.Should().ContainSingle(p => p.Kind == EmailProblemKind.SharedAcrossUsers);
}
```

- [ ] **Step 2: Run, expect fail**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~EmailProblemsServiceTests"`
Expected: FAIL.

- [ ] **Step 3: Add detection AFTER the per-profile loop in `ScanAsync`** (uses existing `EmailNormalization.NormalizeForComparison` from `Humans.Domain.Helpers`)

```csharp
// Cross-user duplicates: build normalized-email -> userIds map, flag pairs.
var normToUsers = new Dictionary<string, List<(Guid UserId, string Raw)>>(StringComparer.Ordinal);
foreach (var p in profiles)
{
    foreach (var email in p.AllUserEmails)
    {
        var norm = EmailNormalization.NormalizeForComparison(email.Email);
        if (!normToUsers.TryGetValue(norm, out var list))
        {
            list = new List<(Guid, string)>();
            normToUsers[norm] = list;
        }
        list.Add((p.UserId, email.Email));
    }
}

foreach (var kvp in normToUsers)
{
    var distinctUsers = kvp.Value.Select(t => t.UserId).Distinct().ToList();
    if (distinctUsers.Count <= 1) continue;

    for (var i = 0; i < distinctUsers.Count; i++)
    {
        for (var j = i + 1; j < distinctUsers.Count; j++)
        {
            var rawA = kvp.Value.First(t => t.UserId == distinctUsers[i]).Raw;
            problems.Add(new EmailProblem(
                EmailProblemKind.SharedAcrossUsers,
                distinctUsers[i], distinctUsers[j],
                null, rawA, null));
        }
    }
}
```

(Add `using Humans.Domain.Helpers;` to the file if not already present.)

- [ ] **Step 4: Run, expect pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~EmailProblemsServiceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/Humans.Application.Tests/Services/Profile/EmailProblemsServiceTests.cs \
        src/Humans.Application/Services/Profile/EmailProblemsService.cs
git commit -m "feat(emailproblems): detect cross-user shared emails with normalization (case 5)"
```

---

### Task 12: Detect case 7 — orphan UserEmail rows (TDD)

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/Profile/EmailProblemsServiceTests.cs`
- Modify: `src/Humans.Application/Services/Profile/EmailProblemsService.cs`

- [ ] **Step 1: Add failing test**

```csharp
[Fact]
public async Task DetectsOrphanUserEmail()
{
    var deadUserId = Guid.NewGuid();
    var emailId = Guid.NewGuid();
    SetProfiles();
    SetOrphans(new UserEmail
    {
        Id = emailId,
        UserId = deadUserId,
        Email = "ghost@x.com",
        IsVerified = true
    });
    SetGhosts();

    var report = await Sut.ScanAsync();

    report.Problems.Should().ContainSingle(p =>
        p.Kind == EmailProblemKind.OrphanUserEmail
        && p.UserEmailId == emailId
        && p.UserId == deadUserId
        && p.Email == "ghost@x.com");
}
```

- [ ] **Step 2: Run, expect fail**

- [ ] **Step 3: In `ScanAsync`, after the cross-user loop, add**

```csharp
var orphans = await _userEmailService.GetOrphanUserEmailsAsync(ct);
foreach (var o in orphans)
{
    problems.Add(new EmailProblem(
        EmailProblemKind.OrphanUserEmail, o.UserId, null, o.Id, o.Email, null));
}
```

- [ ] **Step 4: Run, expect pass**

- [ ] **Step 5: Commit**

```bash
git add tests/Humans.Application.Tests/Services/Profile/EmailProblemsServiceTests.cs \
        src/Humans.Application/Services/Profile/EmailProblemsService.cs
git commit -m "feat(emailproblems): detect orphan UserEmail rows (case 7)"
```

---

### Task 13: Detect case 8 — ghost AspNetUserLogins (TDD)

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/Profile/EmailProblemsServiceTests.cs`
- Modify: `src/Humans.Application/Services/Profile/EmailProblemsService.cs`

- [ ] **Step 1: Add failing test**

```csharp
[Fact]
public async Task DetectsGhostExternalLogins()
{
    var ghostUserId = Guid.NewGuid();
    SetProfiles();
    SetOrphans();
    SetGhosts(ghostUserId);

    var report = await Sut.ScanAsync();

    report.Problems.Should().ContainSingle(p =>
        p.Kind == EmailProblemKind.GhostExternalLogins && p.UserId == ghostUserId);
}
```

- [ ] **Step 2: Run, expect fail**

- [ ] **Step 3: In `ScanAsync`, append**

```csharp
var ghosts = await _userService.GetUsersWithLoginsButNoEmailsAsync(ct);
foreach (var ghostId in ghosts)
{
    problems.Add(new EmailProblem(
        EmailProblemKind.GhostExternalLogins, ghostId, null, null, null, null));
}
```

- [ ] **Step 4: Run, expect pass**

- [ ] **Step 5: Commit**

```bash
git add tests/Humans.Application.Tests/Services/Profile/EmailProblemsServiceTests.cs \
        src/Humans.Application/Services/Profile/EmailProblemsService.cs
git commit -m "feat(emailproblems): detect ghost external logins (case 8)"
```

---

### Task 14: Architecture test — `EmailProblemsService` has zero repository deps

**Files:**
- Modify: `tests/Humans.Application.Tests/Architecture/ProfileArchitectureTests.cs`

- [ ] **Step 1: Add a focused test**

```csharp
[Fact]
public void EmailProblemsService_DependsOnlyOnSectionServices_NotRepositories()
{
    var ctor = typeof(EmailProblemsService).GetConstructors().Single();
    var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

    var allowed = new[]
    {
        typeof(IProfileService),
        typeof(IUserEmailService),
        typeof(IUserService),
        typeof(IClock)
    };

    paramTypes.Should().OnlyContain(t => allowed.Contains(t),
        "EmailProblemsService must use existing section services, never repositories or DbContext");
}
```

(Add `using Humans.Application.Services.Profile; using Humans.Application.Interfaces.Profiles; using Humans.Application.Interfaces.Users; using NodaTime;` and adapt the test class structure to whatever exists in `ProfileArchitectureTests.cs`.)

- [ ] **Step 2: Run, expect pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~EmailProblemsService_DependsOnlyOnSectionServices"`
Expected: PASS.

- [ ] **Step 3: Commit + push the detection phase**

```bash
git add tests/Humans.Application.Tests/Architecture/ProfileArchitectureTests.cs
git commit -m "test(emailproblems): assert no repository deps in service constructor"
git push
```

---

## Phase 3 — Merge kernel extraction

### Task 15: Extract `FoldAsync` private kernel; existing `AcceptAsync` tests stay green

**Files:**
- Modify: `src/Humans.Application/Services/Profile/AccountMergeService.cs`

- [ ] **Step 1: Run the existing AccountMerge test suite, capture passing baseline**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AccountMergeService|FullyQualifiedName~AcceptAsync"`
Expected: PASS (record the count).

- [ ] **Step 2: Add a private `FoldAsync` method that contains the fan-out + tombstone + audit**

Refactor strategy: lift the body of `AcceptAsync` between the request load+validate (top) and the request status update (bottom) into:

```csharp
private record AuditEntry(
    Domain.Enums.AuditAction Action,
    string EntityType,
    Guid EntityId,
    string Description,
    Guid? RelatedEntityId = null,
    string? RelatedEntityType = null);

private async Task FoldAsync(
    Guid sourceUserId, Guid targetUserId,
    Guid adminUserId, AuditEntry audit,
    CancellationToken ct)
{
    var now = _clock.GetCurrentInstant();

    try
    {
        using (var scope = new System.Transactions.TransactionScope(
            System.Transactions.TransactionScopeOption.Required,
            new System.Transactions.TransactionOptions
            {
                IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted
            },
            System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
        {
            foreach (var merger in _userMerges)
                await merger.ReassignAsync(sourceUserId, targetUserId, adminUserId, now, ct);

            await _userService.AnonymizeForMergeAsync(sourceUserId, targetUserId, now, ct);

            await _auditLogService.LogAsync(
                audit.Action,
                audit.EntityType, audit.EntityId,
                audit.Description,
                adminUserId,
                relatedEntityId: audit.RelatedEntityId,
                relatedEntityType: audit.RelatedEntityType);

            scope.Complete();
        }

        _teamService.RemoveMemberFromAllTeamsCache(sourceUserId);
        _roleAssignmentService.InvalidateClaimsCacheForUser(sourceUserId);
        _roleAssignmentService.InvalidateClaimsCacheForUser(targetUserId);
        _roleAssignmentService.InvalidateNavBadgeCache();
        _notificationService.InvalidateBadgeCachesForUsers([sourceUserId, targetUserId]);
    }
    finally
    {
        _teamService.InvalidateActiveTeamsCache();
    }
}
```

Then rewrite `AcceptAsync` to:

```csharp
public async Task AcceptAsync(
    Guid requestId, Guid adminUserId,
    string? notes = null, CancellationToken ct = default)
{
    var request = await _mergeRepository.GetByIdAsync(requestId, ct)
        ?? throw new InvalidOperationException("Merge request not found.");
    if (request.Status != AccountMergeRequestStatus.Pending)
        throw new InvalidOperationException("Merge request is not pending.");

    _logger.LogInformation(
        "Admin {AdminId} accepting merge request {RequestId}: folding {SourceUserId} into {TargetUserId}",
        adminUserId, requestId, request.SourceUserId, request.TargetUserId);

    var audit = new AuditEntry(
        AuditAction.AccountMergeAccepted,
        nameof(AccountMergeRequest), request.Id,
        $"Folded source {request.SourceUserId} into target {request.TargetUserId} — email: {request.Email}",
        RelatedEntityId: request.TargetUserId,
        RelatedEntityType: nameof(User));

    await FoldAsync(request.SourceUserId, request.TargetUserId, adminUserId, audit, ct);

    // Request-specific post-fold steps (must be in a separate scope or merged
    // back into FoldAsync via callback). Mark the pending email verified and
    // update the request row.
    var now = _clock.GetCurrentInstant();
    using (var scope = new System.Transactions.TransactionScope(
        System.Transactions.TransactionScopeOption.Required,
        new System.Transactions.TransactionOptions
        {
            IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted
        },
        System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
    {
        var verified = await _userEmailRepository.MarkVerifiedAsync(request.PendingEmailId, now, ct);
        if (!verified)
            throw new InvalidOperationException(
                $"Pending email {request.PendingEmailId} no longer exists. Cannot complete merge.");

        request.Status = AccountMergeRequestStatus.Accepted;
        request.ResolvedAt = now;
        request.ResolvedByUserId = adminUserId;
        request.AdminNotes = notes;
        await _mergeRepository.UpdateAsync(request, ct);

        scope.Complete();
    }
}
```

> **NOTE on transaction shape:** the original `AcceptAsync` ran fold+pending-verify+request-update in one ambient scope. Splitting them into two scopes is acceptable here because:
>   1. The fold is the irreversible part — once the source is tombstoned, the merge has happened from the user's perspective.
>   2. If the second scope fails (pending email gone, DB hiccup), the request stays Pending and the audit row records "Folded ... — email: X". A retry-or-manual cleanup is a much smaller blast radius than rolling back a fold.
>   3. The simpler alternative — passing a callback into `FoldAsync` so the request-specific work runs inside the same scope — is also fine. Pick whichever the implementer finds cleaner; tests must show identical observable behavior.

- [ ] **Step 3: Run the existing AccountMerge test suite again, expect same count of PASS**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AccountMergeService|FullyQualifiedName~AcceptAsync"`
Expected: PASS — same count as Step 1.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Services/Profile/AccountMergeService.cs
git commit -m "refactor(merge): extract FoldAsync kernel inside AccountMergeService"
```

---

### Task 16: Add `IAccountMergeService.AdminMergeAsync` + tests (TDD)

**Files:**
- Modify: `src/Humans.Application/Interfaces/Profiles/IAccountMergeService.cs`
- Modify: `src/Humans.Application/Services/Profile/AccountMergeService.cs`
- Create or modify: `tests/Humans.Application.Tests/Services/Profile/AccountMergeServiceAdminMergeTests.cs`

- [ ] **Step 1: Add interface method**

```csharp
/// <summary>
/// Admin-initiated merge of two pre-existing accounts (no AccountMergeRequest).
/// Folds <paramref name="sourceUserId"/> into <paramref name="targetUserId"/>:
/// reassigns every section's user-keyed rows via <c>IUserMerge</c>, tombstones
/// the source, and audits. Used by /Profile/Admin/EmailProblems case 5.
/// </summary>
/// <exception cref="InvalidOperationException">
/// Thrown if source==target, either user is missing, or the source is already tombstoned.
/// </exception>
Task AdminMergeAsync(
    Guid sourceUserId, Guid targetUserId,
    Guid adminUserId, string? notes = null,
    CancellationToken ct = default);
```

- [ ] **Step 2: Write failing tests**

Create `tests/Humans.Application.Tests/Services/Profile/AccountMergeServiceAdminMergeTests.cs`. Mirror the structure of any existing `AccountMergeServiceTests.cs` for mock wiring.

```csharp
using FluentAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profile;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodaTime.Testing;
using Xunit;

namespace Humans.Application.Tests.Services.Profile;

public class AccountMergeServiceAdminMergeTests
{
    private readonly Mock<IAccountMergeRepository> _mergeRepo = new();
    private readonly Mock<IUserEmailRepository> _userEmailRepo = new();
    private readonly Mock<IUserService> _userService = new();
    private readonly Mock<IAuditLogService> _audit = new();
    private readonly Mock<ITeamService> _team = new();
    private readonly Mock<IRoleAssignmentService> _roles = new();
    private readonly Mock<INotificationService> _notify = new();
    private readonly List<Mock<IUserMerge>> _userMerges = new();
    private readonly FakeClock _clock = new(NodaTime.Instant.FromUtc(2026, 5, 5, 12, 0));

    private AccountMergeService BuildSut() =>
        new(
            _mergeRepo.Object, _userEmailRepo.Object, _userService.Object,
            _audit.Object, _team.Object, _roles.Object, _notify.Object,
            _userMerges.Select(m => m.Object), _clock,
            NullLogger<AccountMergeService>.Instance /* + any other deps the real ctor takes — adjust */);

    private void SetupUsers(Guid sourceId, Guid targetId, bool sourceTombstoned = false)
    {
        _userService.Setup(s => s.GetByIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = sourceId, MergedToUserId = sourceTombstoned ? targetId : null });
        _userService.Setup(s => s.GetByIdAsync(targetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = targetId });
    }

    [Fact]
    public async Task AdminMergeAsync_HappyPath_RunsFanOutAndTombstone()
    {
        var src = Guid.NewGuid(); var tgt = Guid.NewGuid(); var admin = Guid.NewGuid();
        SetupUsers(src, tgt);
        var merger = new Mock<IUserMerge>(); _userMerges.Add(merger);

        await BuildSut().AdminMergeAsync(src, tgt, admin);

        merger.Verify(m => m.ReassignAsync(src, tgt, admin,
            It.IsAny<NodaTime.Instant>(), It.IsAny<CancellationToken>()), Times.Once);
        _userService.Verify(s => s.AnonymizeForMergeAsync(src, tgt,
            It.IsAny<NodaTime.Instant>(), It.IsAny<CancellationToken>()), Times.Once);
        _userEmailRepo.Verify(r => r.MarkVerifiedAsync(
            It.IsAny<Guid>(), It.IsAny<NodaTime.Instant>(), It.IsAny<CancellationToken>()),
            Times.Never, "AdminMergeAsync must NOT call MarkVerifiedAsync");
    }

    [Fact]
    public async Task AdminMergeAsync_SourceEqualsTarget_Throws()
    {
        var id = Guid.NewGuid();
        var act = () => BuildSut().AdminMergeAsync(id, id, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*same*");
    }

    [Fact]
    public async Task AdminMergeAsync_SourceMissing_Throws()
    {
        var src = Guid.NewGuid(); var tgt = Guid.NewGuid();
        _userService.Setup(s => s.GetByIdAsync(tgt, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = tgt });
        var act = () => BuildSut().AdminMergeAsync(src, tgt, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AdminMergeAsync_SourceAlreadyTombstoned_Throws()
    {
        var src = Guid.NewGuid(); var tgt = Guid.NewGuid();
        SetupUsers(src, tgt, sourceTombstoned: true);
        var act = () => BuildSut().AdminMergeAsync(src, tgt, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*tombstoned*");
    }
}
```

(Adapt the `BuildSut` ctor params to match the actual `AccountMergeService` constructor signature — read it from the source first.)

- [ ] **Step 3: Run, expect 4 fails (AdminMergeAsync doesn't exist yet)**

- [ ] **Step 4: Implement `AdminMergeAsync` on `AccountMergeService`**

```csharp
public async Task AdminMergeAsync(
    Guid sourceUserId, Guid targetUserId,
    Guid adminUserId, string? notes = null,
    CancellationToken ct = default)
{
    if (sourceUserId == targetUserId)
        throw new InvalidOperationException("Source and target users are the same.");

    var source = await _userService.GetByIdAsync(sourceUserId, ct)
        ?? throw new InvalidOperationException($"Source user {sourceUserId} not found.");
    var target = await _userService.GetByIdAsync(targetUserId, ct)
        ?? throw new InvalidOperationException($"Target user {targetUserId} not found.");

    if (source.MergedToUserId is not null)
        throw new InvalidOperationException(
            $"Source user {sourceUserId} is already tombstoned (merged into {source.MergedToUserId}).");

    if (target.MergedToUserId is not null)
        throw new InvalidOperationException(
            $"Target user {targetUserId} is already tombstoned.");

    _logger.LogInformation(
        "Admin {AdminId} initiated direct merge: folding {SourceUserId} into {TargetUserId}",
        adminUserId, sourceUserId, targetUserId);

    var description = $"Admin-initiated via EmailProblems: folded source {sourceUserId} into target {targetUserId}. Notes: {notes ?? "(none)"}";

    var audit = new AuditEntry(
        AuditAction.AccountMergeAccepted,
        nameof(User), sourceUserId,
        description,
        RelatedEntityId: targetUserId,
        RelatedEntityType: nameof(User));

    await FoldAsync(sourceUserId, targetUserId, adminUserId, audit, ct);
}
```

- [ ] **Step 5: Run tests, expect pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AccountMergeServiceAdminMergeTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Interfaces/Profiles/IAccountMergeService.cs \
        src/Humans.Application/Services/Profile/AccountMergeService.cs \
        tests/Humans.Application.Tests/Services/Profile/AccountMergeServiceAdminMergeTests.cs
git commit -m "feat(merge): add IAccountMergeService.AdminMergeAsync (admin-initiated fold)"
```

---

### Task 17: Integration test for full-fixture admin-initiated merge

**Files:**
- Create: `tests/Humans.Integration.Tests/AccountMerge/AdminMergeAsyncTests.cs`

- [ ] **Step 1: Read the existing `tests/Humans.Integration.Tests/AccountMerge/AcceptAsyncFullFixtureTest.cs` and `MergeFixtureBuilder.cs` to understand fixture conventions**

(Discovery step — no edit yet. Use `cat` or Read.)

- [ ] **Step 2: Create the new test file mirroring `AcceptAsyncFullFixtureTest`'s setup, but invoking `AdminMergeAsync(sourceId, targetId, adminId)` instead of `AcceptAsync(requestId, adminId)`**

The fixture must seed:
- Two users, both with verified UserEmail rows, where source has `joe@x.com` (verified, IsPrimary on source) and target has `target@x.com` (verified, IsPrimary on target).
- Source has at least one team membership and one role assignment so the fan-out has work to do.
- Source has at least one AspNetUserLogins row so reassignment is verifiable.

Assertions after the call:
- Exactly one UserEmail row per normalized email exists in the database.
- Source has zero UserEmail rows.
- Source has zero AspNetUserLogins rows.
- Target's pre-existing `IsPrimary` UserEmail still has `IsPrimary = true`; target's pre-existing `IsGoogle` (if any) still has `IsGoogle = true`.
- Source User row has `MergedToUserId = targetId` and `MergedAt` set.
- An `AuditAction.AccountMergeAccepted` row exists with `EntityType == nameof(User)`, `EntityId == sourceId`, and the description starts with `"Admin-initiated via EmailProblems"`.

(Use the spec's "Acceptance Criteria — case 5" wording verbatim as test names so the spec ↔ test mapping is obvious.)

- [ ] **Step 3: Run integration test**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AdminMergeAsyncTests"`
Expected: PASS.

- [ ] **Step 4: Commit + push**

```bash
git add tests/Humans.Integration.Tests/AccountMerge/AdminMergeAsyncTests.cs
git commit -m "test(merge): full-fixture integration coverage for AdminMergeAsync"
git push
```

---

## Phase 4 — Controller, view models, views

### Task 18: Create `ProfileAdminController` skeleton

**Files:**
- Create: `src/Humans.Web/Controllers/ProfileAdminController.cs`

- [ ] **Step 1: Write the skeleton**

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces.Profiles;
using Humans.Domain.Constants;
using Humans.Domain.Entities;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Profile/Admin")]
public class ProfileAdminController : HumansControllerBase
{
    private readonly IEmailProblemsService _emailProblems;
    private readonly IAccountMergeService _accountMerge;
    private readonly IUserEmailService _userEmails;
    private readonly IUserService _users;
    private readonly IAuditLogService _audit;
    private readonly ILogger<ProfileAdminController> _logger;

    public ProfileAdminController(
        UserManager<User> userManager,
        IEmailProblemsService emailProblems,
        IAccountMergeService accountMerge,
        IUserEmailService userEmails,
        IUserService users,
        IAuditLogService audit,
        ILogger<ProfileAdminController> logger)
        : base(userManager)
    {
        _emailProblems = emailProblems;
        _accountMerge = accountMerge;
        _userEmails = userEmails;
        _users = users;
        _audit = audit;
        _logger = logger;
    }
}
```

(Add `using` for `IUserEmailService`, `IUserService`, `IAuditLogService`, `IAccountMergeService` from their respective namespaces.)

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Controllers/ProfileAdminController.cs
git commit -m "feat(profileadmin): scaffold ProfileAdminController"
```

---

### Task 19: View models for list + compare

**Files:**
- Create: `src/Humans.Web/Models/EmailProblems/EmailProblemsListViewModel.cs`
- Create: `src/Humans.Web/Models/EmailProblems/EmailProblemsCompareViewModel.cs`

- [ ] **Step 1: Write list view model**

```csharp
using Humans.Application.DTOs.EmailProblems;
using NodaTime;

namespace Humans.Web.Models.EmailProblems;

public sealed class EmailProblemsListViewModel
{
    public Instant ScannedAt { get; init; }
    public int TotalProblems => CrossUserConflicts.Count + SingleUserIssues.Count + SystemLevelIssues.Count;

    public IReadOnlyList<CrossUserConflictRow> CrossUserConflicts { get; init; } = Array.Empty<CrossUserConflictRow>();
    public IReadOnlyList<SingleUserIssueRow> SingleUserIssues { get; init; } = Array.Empty<SingleUserIssueRow>();
    public IReadOnlyList<SystemLevelIssueRow> SystemLevelIssues { get; init; } = Array.Empty<SystemLevelIssueRow>();
}

public sealed record CrossUserConflictRow(
    string Email,
    Guid User1Id, string User1DisplayName,
    Guid User2Id, string User2DisplayName);

public sealed record SingleUserIssueRow(
    Guid UserId, string DisplayName,
    IReadOnlyList<string> ProblemSummaries);

public sealed record SystemLevelIssueRow(
    EmailProblemKind Kind,
    Guid? UserEmailId,
    Guid? UserId,
    string Detail);
```

- [ ] **Step 2: Write compare view model**

```csharp
using Humans.Application;
using Humans.Domain.Entities;

namespace Humans.Web.Models.EmailProblems;

public sealed class EmailProblemsCompareViewModel
{
    public required string SharedEmail { get; init; }
    public required CompareSide Account1 { get; init; }
    public required CompareSide Account2 { get; init; }
}

public sealed record CompareSide(
    Guid UserId,
    string DisplayName,
    string? ProfilePictureUrl,
    IReadOnlyList<UserEmailSnapshot> AllUserEmails,
    int TeamCount,
    int RoleAssignmentCount,
    DateTime? LastLogin,
    bool IsProfileComplete);
```

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Models/EmailProblems/
git commit -m "feat(profileadmin): EmailProblems list + compare view models"
```

---

### Task 20: GET `/Profile/Admin/EmailProblems` — list view

**Files:**
- Modify: `src/Humans.Web/Controllers/ProfileAdminController.cs`
- Create: `src/Humans.Web/Views/ProfileAdmin/EmailProblems.cshtml`

- [ ] **Step 1: Add the GET action**

Inside `ProfileAdminController`, add:

```csharp
[HttpGet("EmailProblems")]
public async Task<IActionResult> EmailProblems(CancellationToken ct)
{
    var report = await _emailProblems.ScanAsync(ct);

    // Map: cross-user, single-user (grouped by user), system-level.
    var crossUser = new List<CrossUserConflictRow>();
    var singleUserMap = new Dictionary<Guid, List<string>>();
    var systemLevel = new List<SystemLevelIssueRow>();

    var allInvolvedUserIds = report.Problems
        .SelectMany(p => new[] { p.UserId, p.OtherUserId })
        .OfType<Guid>()
        .Distinct()
        .ToList();
    var users = await _users.GetByIdsAsync(allInvolvedUserIds, ct);
    string DisplayName(Guid? id) =>
        id is Guid g && users.TryGetValue(g, out var u) ? u.DisplayName : "(unknown)";

    foreach (var p in report.Problems)
    {
        switch (p.Kind)
        {
            case EmailProblemKind.SharedAcrossUsers when p.UserId is Guid u1 && p.OtherUserId is Guid u2:
                crossUser.Add(new CrossUserConflictRow(p.Email ?? "(unknown)", u1, DisplayName(u1), u2, DisplayName(u2)));
                break;

            case EmailProblemKind.MultipleIsPrimary or EmailProblemKind.MultipleIsGoogle
                or EmailProblemKind.ZeroIsPrimary or EmailProblemKind.ZeroIsGoogle
                or EmailProblemKind.Unverified
                when p.UserId is Guid u:
                if (!singleUserMap.TryGetValue(u, out var list))
                {
                    list = new List<string>();
                    singleUserMap[u] = list;
                }
                list.Add(p.Kind switch
                {
                    EmailProblemKind.MultipleIsPrimary => "multiple IsPrimary",
                    EmailProblemKind.MultipleIsGoogle => "multiple IsGoogle",
                    EmailProblemKind.ZeroIsPrimary => "zero IsPrimary",
                    EmailProblemKind.ZeroIsGoogle => "zero IsGoogle",
                    EmailProblemKind.Unverified => $"unverified: {p.Email}",
                    _ => p.Kind.ToString()
                });
                break;

            case EmailProblemKind.OrphanUserEmail:
                systemLevel.Add(new SystemLevelIssueRow(
                    p.Kind, p.UserEmailId, p.UserId,
                    $"Orphan UserEmail \"{p.Email}\" (was userId {p.UserId})"));
                break;

            case EmailProblemKind.GhostExternalLogins:
                systemLevel.Add(new SystemLevelIssueRow(
                    p.Kind, null, p.UserId,
                    $"Ghost AspNetUserLogins for userId {p.UserId}"));
                break;
        }
    }

    var singleUser = singleUserMap
        .Select(kvp => new SingleUserIssueRow(kvp.Key, DisplayName(kvp.Key), kvp.Value))
        .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    var vm = new EmailProblemsListViewModel
    {
        ScannedAt = report.ScannedAt,
        CrossUserConflicts = crossUser,
        SingleUserIssues = singleUser,
        SystemLevelIssues = systemLevel
    };

    return View(vm);
}
```

- [ ] **Step 2: Add the view**

`src/Humans.Web/Views/ProfileAdmin/EmailProblems.cshtml`:

```cshtml
@using Humans.Web.Models.EmailProblems
@using Humans.Application.DTOs.EmailProblems
@model EmailProblemsListViewModel
@{
    ViewData["Title"] = "Email Problems";
}

<h1>Email Problems</h1>
<p class="text-muted">
    Scanned at @Model.ScannedAt.ToDateTimeUtc().ToString("HH:mm:ss") · @Model.TotalProblems total problems
</p>

<h2>Cross-user conflicts</h2>
@if (Model.CrossUserConflicts.Count == 0)
{
    <p class="text-success">No problems found</p>
}
else
{
    <table class="table">
        <thead>
            <tr><th>Email</th><th>User A</th><th>User B</th><th></th></tr>
        </thead>
        <tbody>
            @foreach (var row in Model.CrossUserConflicts)
            {
                <tr>
                    <td><code>@row.Email</code></td>
                    <td>@row.User1DisplayName</td>
                    <td>@row.User2DisplayName</td>
                    <td>
                        <a class="btn btn-sm btn-primary"
                           asp-action="EmailProblemsCompare"
                           asp-route-userId1="@row.User1Id"
                           asp-route-userId2="@row.User2Id">Compare</a>
                    </td>
                </tr>
            }
        </tbody>
    </table>
}

<h2>Single-user issues</h2>
@if (Model.SingleUserIssues.Count == 0)
{
    <p class="text-success">No problems found</p>
}
else
{
    <table class="table">
        <thead><tr><th>User</th><th>Problems</th><th></th></tr></thead>
        <tbody>
            @foreach (var row in Model.SingleUserIssues)
            {
                <tr>
                    <td>@row.DisplayName</td>
                    <td>@string.Join(", ", row.ProblemSummaries)</td>
                    <td>
                        <a class="btn btn-sm btn-secondary"
                           href="/Profile/@row.UserId/Admin/Emails">Open emails ▸</a>
                    </td>
                </tr>
            }
        </tbody>
    </table>
}

<h2>System-level issues</h2>
@if (Model.SystemLevelIssues.Count == 0)
{
    <p class="text-success">No problems found</p>
}
else
{
    <table class="table">
        <thead><tr><th>Issue</th><th></th></tr></thead>
        <tbody>
            @foreach (var row in Model.SystemLevelIssues)
            {
                <tr>
                    <td>@row.Detail</td>
                    <td>
                        @if (row.Kind == EmailProblemKind.OrphanUserEmail)
                        {
                            <form method="post" asp-action="DeleteOrphanEmail"
                                  onsubmit="return confirm('Delete orphan UserEmail row? This is irreversible.');"
                                  class="d-inline">
                                @Html.AntiForgeryToken()
                                <input type="hidden" name="emailId" value="@row.UserEmailId" />
                                <button type="submit" class="btn btn-sm btn-danger">Delete row ✕</button>
                            </form>
                        }
                        else if (row.Kind == EmailProblemKind.GhostExternalLogins)
                        {
                            <form method="post" asp-action="DeleteGhostLogins"
                                  onsubmit="return confirm('Delete ghost AspNetUserLogins rows? This is irreversible.');"
                                  class="d-inline">
                                @Html.AntiForgeryToken()
                                <input type="hidden" name="userId" value="@row.UserId" />
                                <button type="submit" class="btn btn-sm btn-danger">Delete logins ✕</button>
                            </form>
                        }
                    </td>
                </tr>
            }
        </tbody>
    </table>
}
```

- [ ] **Step 3: Build, run app locally, navigate to `/Profile/Admin/EmailProblems` as an admin**

Run: `dotnet build Humans.slnx -v quiet`
Manual: `dotnet run --project src/Humans.Web` then visit `https://localhost:<port>/Profile/Admin/EmailProblems` signed in as an admin. Page should render with three sections.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/ProfileAdminController.cs \
        src/Humans.Web/Views/ProfileAdmin/EmailProblems.cshtml
git commit -m "feat(profileadmin): EmailProblems list view (cases 1–8 grouped)"
```

---

### Task 21: GET `/Profile/Admin/EmailProblems/Compare`

**Files:**
- Modify: `src/Humans.Web/Controllers/ProfileAdminController.cs`
- Create: `src/Humans.Web/Views/ProfileAdmin/EmailProblemsCompare.cshtml`

- [ ] **Step 1: Add the action**

```csharp
[HttpGet("EmailProblems/Compare")]
public async Task<IActionResult> EmailProblemsCompare(Guid userId1, Guid userId2, CancellationToken ct)
{
    if (userId1 == userId2)
    {
        SetError("Cannot compare a user against themselves.");
        return RedirectToAction(nameof(EmailProblems));
    }

    var ids = new[] { userId1, userId2 };
    var users = await _users.GetByIdsAsync(ids, ct);
    if (!users.TryGetValue(userId1, out var u1) || !users.TryGetValue(userId2, out var u2))
    {
        SetError("One or both users not found.");
        return RedirectToAction(nameof(EmailProblems));
    }

    // Pull each user's full email list via the FullProfile cache (no repo).
    var profiles = await _profileService.GetFullProfileSnapshotAsync(ct);
    var byUser = profiles.ToDictionary(p => p.UserId);
    byUser.TryGetValue(userId1, out var p1);
    byUser.TryGetValue(userId2, out var p2);

    // Find shared email for the heading.
    var sharedEmail = (p1?.AllUserEmails ?? Array.Empty<UserEmailSnapshot>())
        .Select(e => e.Email)
        .Intersect((p2?.AllUserEmails ?? Array.Empty<UserEmailSnapshot>()).Select(e => e.Email),
                   StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault() ?? "(no exact match — see normalized)";

    CompareSide BuildSide(User user, FullProfile? profile, int teamCount, int roleCount) =>
        new(user.Id, user.DisplayName, user.ProfilePictureUrl,
            profile?.AllUserEmails ?? Array.Empty<UserEmailSnapshot>(),
            teamCount, roleCount,
            user.LastLoginAt?.ToDateTimeUtc(),
            !string.IsNullOrEmpty(profile?.BurnerName));

    // For TeamCount + RoleAssignmentCount, mirror what AdminDuplicateAccountsController does.
    // Read from existing services — do NOT add new ones here.
    var memberships1 = await _teamService.GetUserTeamsAsync(userId1, ct);
    var memberships2 = await _teamService.GetUserTeamsAsync(userId2, ct);
    var roles1 = await _roleAssignmentService.GetByUserIdAsync(userId1, ct);
    var roles2 = await _roleAssignmentService.GetByUserIdAsync(userId2, ct);

    var vm = new EmailProblemsCompareViewModel
    {
        SharedEmail = sharedEmail,
        Account1 = BuildSide(u1, p1,
            memberships1.Count(m => m.LeftAt is null),
            roles1.Count(r => r.ValidTo is null)),
        Account2 = BuildSide(u2, p2,
            memberships2.Count(m => m.LeftAt is null),
            roles2.Count(r => r.ValidTo is null))
    };

    return View(vm);
}
```

(Add `IProfileService _profileService`, `ITeamService _teamService`, `IRoleAssignmentService _roleAssignmentService` injections to the constructor.)

- [ ] **Step 2: Add the view**

`src/Humans.Web/Views/ProfileAdmin/EmailProblemsCompare.cshtml`:

```cshtml
@using Humans.Web.Models.EmailProblems
@model EmailProblemsCompareViewModel
@{
    ViewData["Title"] = "Compare accounts";
}

<h1>Compare for shared email</h1>
<p class="text-muted">Shared email: <code>@Model.SharedEmail</code></p>

<form method="post" asp-action="Merge"
      onsubmit="return confirm('Merge these accounts? The selected SOURCE will be tombstoned. This is irreversible.');">
    @Html.AntiForgeryToken()

    <div class="row">
        @foreach (var (side, label, isLeft) in new[] {
            (Model.Account1, "Account A", true),
            (Model.Account2, "Account B", false) })
        {
            <div class="col-md-6">
                <h2>@label — @side.DisplayName</h2>
                <p>
                    Teams: @side.TeamCount · Roles: @side.RoleAssignmentCount
                    · Last login: @(side.LastLogin?.ToString("yyyy-MM-dd") ?? "—")
                    · Profile complete: @(side.IsProfileComplete ? "yes" : "no")
                </p>

                <table class="table table-sm">
                    <thead>
                        <tr><th>Email</th><th>Verified</th><th>Primary</th><th>Google</th></tr>
                    </thead>
                    <tbody>
                        @foreach (var e in side.AllUserEmails)
                        {
                            <tr>
                                <td><code>@e.Email</code></td>
                                <td>@(e.IsVerified ? "✓" : "")</td>
                                <td>@(e.IsPrimary ? "✓" : "")</td>
                                <td>@(e.IsGoogle ? "✓" : "")</td>
                            </tr>
                        }
                    </tbody>
                </table>

                <label class="form-check">
                    <input class="form-check-input" type="radio" name="targetUserId" value="@side.UserId" required />
                    Keep <strong>@side.DisplayName</strong> as target
                </label>
            </div>
        }
    </div>

    <div class="mt-3">
        <label for="notes" class="form-label">Admin notes (optional)</label>
        <textarea id="notes" name="notes" class="form-control" rows="2"></textarea>
    </div>

    <input type="hidden" name="user1Id" value="@Model.Account1.UserId" />
    <input type="hidden" name="user2Id" value="@Model.Account2.UserId" />

    <button type="submit" class="btn btn-danger mt-3">Merge</button>
    <a class="btn btn-secondary mt-3" asp-action="EmailProblems">Cancel</a>
</form>
```

- [ ] **Step 3: Build, manually navigate to `Compare?userId1=...&userId2=...` for a known case 5 pair**

Run: `dotnet build Humans.slnx -v quiet`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/ProfileAdminController.cs \
        src/Humans.Web/Views/ProfileAdmin/EmailProblemsCompare.cshtml
git commit -m "feat(profileadmin): EmailProblems Compare view (case 5 detail)"
```

---

### Task 22: POST `/Profile/Admin/EmailProblems/Merge`

**Files:**
- Modify: `src/Humans.Web/Controllers/ProfileAdminController.cs`

- [ ] **Step 1: Add the action**

```csharp
[HttpPost("EmailProblems/Merge")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> Merge(
    Guid user1Id, Guid user2Id, Guid targetUserId, string? notes,
    CancellationToken ct)
{
    var (error, currentUser) = await RequireCurrentUserAsync();
    if (error is not null) return error;

    Guid sourceUserId;
    if (targetUserId == user1Id) sourceUserId = user2Id;
    else if (targetUserId == user2Id) sourceUserId = user1Id;
    else
    {
        SetError("Target must be one of the two compared accounts.");
        return RedirectToAction(nameof(EmailProblemsCompare),
            new { userId1 = user1Id, userId2 = user2Id });
    }

    try
    {
        await _accountMerge.AdminMergeAsync(sourceUserId, targetUserId, currentUser.Id, notes, ct);
        SetSuccess("Accounts merged. The source account has been tombstoned.");
        return RedirectToAction(nameof(EmailProblems));
    }
    catch (InvalidOperationException ex)
    {
        _logger.LogError(ex, "Admin-initiated merge failed: source {Source}, target {Target}",
            sourceUserId, targetUserId);
        SetError($"Merge failed: {ex.Message}");
        return RedirectToAction(nameof(EmailProblemsCompare),
            new { userId1 = user1Id, userId2 = user2Id });
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Controllers/ProfileAdminController.cs
git commit -m "feat(profileadmin): POST EmailProblems/Merge"
```

---

### Task 23: POST `/Profile/Admin/EmailProblems/DeleteOrphanEmail`

**Files:**
- Modify: `src/Humans.Application/Interfaces/Profiles/IUserEmailService.cs`
- Modify: `src/Humans.Application/Services/Profile/UserEmailService.cs`
- Modify: `src/Humans.Web/Controllers/ProfileAdminController.cs`

- [ ] **Step 1: Add a service method to delete an orphan by email id**

In `IUserEmailService.cs`:

```csharp
/// <summary>
/// Deletes a single UserEmail row by id. Used by EmailProblems orphan cleanup.
/// Idempotent — returns false if the row no longer exists.
/// </summary>
Task<bool> DeleteByIdAsync(Guid emailId, CancellationToken ct = default);
```

In `UserEmailService.cs`:

```csharp
public Task<bool> DeleteByIdAsync(Guid emailId, CancellationToken ct = default) =>
    _repository.RemoveByIdAsync(emailId, ct);
```

(`RemoveByIdAsync` already exists on `IUserEmailRepository` per existing code reading.)

- [ ] **Step 2: Add the controller action**

```csharp
[HttpPost("EmailProblems/DeleteOrphanEmail")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> DeleteOrphanEmail(Guid emailId, CancellationToken ct)
{
    var (error, currentUser) = await RequireCurrentUserAsync();
    if (error is not null) return error;

    var deleted = await _userEmails.DeleteByIdAsync(emailId, ct);
    if (deleted)
    {
        await _audit.LogAsync(
            AuditAction.OrphanUserEmailDeleted, nameof(UserEmail), emailId,
            $"Orphan UserEmail row {emailId} deleted by EmailProblems action",
            currentUser.Id);
        SetSuccess("Orphan email row deleted.");
    }
    else
    {
        SetInfo("Already cleaned up — no row to delete.");
    }
    return RedirectToAction(nameof(EmailProblems));
}
```

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Interfaces/Profiles/IUserEmailService.cs \
        src/Humans.Application/Services/Profile/UserEmailService.cs \
        src/Humans.Web/Controllers/ProfileAdminController.cs
git commit -m "feat(profileadmin): POST EmailProblems/DeleteOrphanEmail"
```

---

### Task 24: POST `/Profile/Admin/EmailProblems/DeleteGhostLogins`

**Files:**
- Modify: `src/Humans.Application/Interfaces/Repositories/IUserRepository.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Users/UserRepository.cs`
- Modify: `src/Humans.Application/Interfaces/Users/IUserService.cs`
- Modify: `src/Humans.Application/Services/Users/UserService.cs`
- Modify: `src/Humans.Web/Controllers/ProfileAdminController.cs`

- [ ] **Step 1: Add repo method**

In `IUserRepository.cs`:

```csharp
/// <summary>
/// Deletes every <c>AspNetUserLogins</c> row for the given user. Returns the
/// number of rows deleted. Used by EmailProblems ghost-login cleanup.
/// </summary>
Task<int> DeleteAllExternalLoginsForUserAsync(Guid userId, CancellationToken ct = default);
```

In `UserRepository.cs`:

```csharp
public async Task<int> DeleteAllExternalLoginsForUserAsync(Guid userId, CancellationToken ct = default)
{
    using var ctx = await _contextFactory.CreateDbContextAsync(ct);
    return await ctx.UserLogins
        .Where(l => l.UserId == userId)
        .ExecuteDeleteAsync(ct);
}
```

- [ ] **Step 2: Pass-through on `IUserService` / `UserService`**

```csharp
// IUserService
Task<int> DeleteAllExternalLoginsForUserAsync(Guid userId, CancellationToken ct = default);

// UserService
public Task<int> DeleteAllExternalLoginsForUserAsync(Guid userId, CancellationToken ct = default) =>
    _repo.DeleteAllExternalLoginsForUserAsync(userId, ct);
```

- [ ] **Step 3: Add the controller action**

```csharp
[HttpPost("EmailProblems/DeleteGhostLogins")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> DeleteGhostLogins(Guid userId, CancellationToken ct)
{
    var (error, currentUser) = await RequireCurrentUserAsync();
    if (error is not null) return error;

    var count = await _users.DeleteAllExternalLoginsForUserAsync(userId, ct);
    if (count > 0)
    {
        await _audit.LogAsync(
            AuditAction.GhostExternalLoginsDeleted, nameof(User), userId,
            $"Deleted {count} ghost AspNetUserLogins row(s) for userId {userId}",
            currentUser.Id);
        SetSuccess($"Deleted {count} ghost login row(s).");
    }
    else
    {
        SetInfo("Already cleaned up — no rows to delete.");
    }
    return RedirectToAction(nameof(EmailProblems));
}
```

- [ ] **Step 4: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: PASS.

- [ ] **Step 5: Commit + push the controller phase**

```bash
git add src/Humans.Application/Interfaces/Repositories/IUserRepository.cs \
        src/Humans.Infrastructure/Repositories/Users/UserRepository.cs \
        src/Humans.Application/Interfaces/Users/IUserService.cs \
        src/Humans.Application/Services/Users/UserService.cs \
        src/Humans.Web/Controllers/ProfileAdminController.cs
git commit -m "feat(profileadmin): POST EmailProblems/DeleteGhostLogins"
git push
```

---

### Task 25: Controller tests — AdminOnly enforcement + happy paths

**Files:**
- Create: `tests/Humans.Application.Tests/Controllers/ProfileAdminControllerTests.cs`

- [ ] **Step 1: Read existing controller test patterns** (e.g., `ProfileControllerEmailGridTests.cs`) to match wiring style.

- [ ] **Step 2: Write the test class**

```csharp
using FluentAssertions;
using Humans.Application.DTOs.EmailProblems;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Application.Tests.Controllers;

public class ProfileAdminControllerTests
{
    // Test 1: GET EmailProblems calls scan, returns view with vm
    // Test 2: POST Merge with target == user1Id calls AdminMergeAsync(user2Id → user1Id)
    // Test 3: POST Merge with target not in pair → redirect with error, no merge call
    // Test 4: POST DeleteOrphanEmail success → audit logged, success toast
    // Test 5: POST DeleteOrphanEmail row missing → info toast, no audit
    // Test 6: POST DeleteGhostLogins success → audit logged with count
    //
    // Each test wires UserManager<User>, IEmailProblemsService, IAccountMergeService,
    // IUserEmailService, IUserService, IAuditLogService mocks; instantiates
    // ProfileAdminController; calls the action; asserts on returned IActionResult
    // and Mock.Verify calls.
}
```

(Flesh out each scenario per the comments. The exact mock wiring will mirror an existing test file in the same folder — copy its `BuildController()` helper and adjust.)

- [ ] **Step 3: Run, expect pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~ProfileAdminControllerTests"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/Humans.Application.Tests/Controllers/ProfileAdminControllerTests.cs
git commit -m "test(profileadmin): controller tests for EmailProblems + actions"
```

---

## Phase 5 — Wire-up + docs

### Task 26: Add the admin nav link

**Files:**
- Modify: existing admin nav source — search for where `/Admin/DuplicateAccounts` is linked from. Likely `src/Humans.Web/ViewComponents/AdminNavTree.cs` (matched in Phase 1 grep).

- [ ] **Step 1: Read `src/Humans.Web/ViewComponents/AdminNavTree.cs` to find how existing admin entries are added**

- [ ] **Step 2: Add an entry for `/Profile/Admin/EmailProblems` under the same parent (likely "Profiles" or "Admin → Profiles")**

Match the existing entry shape exactly. Title: "Email Problems".

- [ ] **Step 3: Build, run app, confirm link appears in admin nav and routes correctly**

Run: `dotnet build Humans.slnx -v quiet`
Manual: `dotnet run --project src/Humans.Web` then visit a page that shows the admin nav while signed in as admin.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/ViewComponents/AdminNavTree.cs
git commit -m "feat(profileadmin): link EmailProblems from admin nav"
```

---

### Task 27: Update `docs/sections/Profiles.md` route table

**Files:**
- Modify: `docs/sections/Profiles.md`

- [ ] **Step 1: In the "Routing" section, add an entry under the existing `/Profile/Admin` block**

```markdown
| `/Profile/Admin/EmailProblems` | List UserEmail invariant violations across all accounts (`ProfileAdminController`) |
| `/Profile/Admin/EmailProblems/Compare` | Side-by-side detail for a case-5 cross-user email collision |
| `/Profile/Admin/EmailProblems/Merge` | POST — admin-initiated merge via `IAccountMergeService.AdminMergeAsync` |
| `/Profile/Admin/EmailProblems/DeleteOrphanEmail` | POST — delete a single orphan UserEmail row |
| `/Profile/Admin/EmailProblems/DeleteGhostLogins` | POST — delete every AspNetUserLogins row for a userId with no UserEmails |
```

- [ ] **Step 2: In "Concepts", add a one-liner near "Duplicate Account Detection"**

```markdown
- **Email Problems** scans every UserEmail invariant violation (multi/zero IsPrimary or IsGoogle, unverified rows, cross-user collisions, orphan rows, ghost AspNetUserLogins). Reads source-of-truth from the `FullProfile` cache. Read-only-plus-three-actions admin surface; the cross-user merge action shares its kernel with `IAccountMergeService.AcceptAsync`.
```

- [ ] **Step 3: Update the "Architecture > Owning services" line** to include `EmailProblemsService`.

- [ ] **Step 4: Commit**

```bash
git add docs/sections/Profiles.md
git commit -m "docs(profile): document /Profile/Admin/EmailProblems"
```

---

### Task 28: Final build + manual smoke

- [ ] **Step 1: Full build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: PASS.

- [ ] **Step 2: Full test suite**

Run: `dotnet test Humans.slnx -v quiet`
Expected: PASS.

- [ ] **Step 3: Run app, sign in as admin, walk all 8 cases**

```bash
dotnet run --project src/Humans.Web
```

For each case:
- Seed a violating row in dev DB (or use existing real data)
- Visit `/Profile/Admin/EmailProblems`
- Confirm the case shows in the right section (cross-user / single-user / system-level)
- For case 5: click Compare, confirm side-by-side renders all UserEmail rows with badges, no `User.Email` shown anywhere
- For case 5: pick a target, click Merge, confirm the source is tombstoned and one UserEmail row remains per email
- For case 7: click Delete row, confirm orphan removed
- For case 8: click Delete logins, confirm AspNetUserLogins rows removed
- Confirm "No problems found" appears in any section that has no problems

- [ ] **Step 4: Push final state**

```bash
git push
```

- [ ] **Step 5: Open PR to peter's `main`**

Use `gh pr create` against `peterdrier/Humans:main`. Title: `issue-660: /Profile/Admin/EmailProblems page`. Body summarizes the 8 cases + the kernel extraction + the link to the spec doc on this branch.

---

## Self-review notes

- **Spec coverage:** every spec section maps to at least one task. Cases 1–8 each have a TDD task. Kernel extraction in Task 15. AdminMergeAsync in Task 16. Compare page in Task 21. Audit actions added in Task 6 and used in Tasks 23/24. Architecture test in Task 14.
- **Type consistency:** `EmailProblem.UserEmailId` (Guid?) used in tasks 7/10/12 matches usage in 20/23. `EmailProblemKind` enum stable across tasks. `AuditEntry` record introduced in Task 15 reused by Task 16.
- **One ambiguity acknowledged in-plan:** Task 15 leaves the choice between "two scopes" and "callback into FoldAsync" up to the implementer with explicit constraints; both produce identical observable behavior. This is a deliberate flexibility, not a placeholder.
- **No raw `User.Email` reads** in any task. Compare view (Task 21) explicitly uses `FullProfile.AllUserEmails`.
