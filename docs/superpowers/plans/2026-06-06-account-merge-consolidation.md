# Account Merge Consolidation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the three overlapping duplicate/merge admin paths with one ordered merge engine and one admin surface (`/Users/Admin/AccountMerges`), fixing wrong-direction merges, orphaned requests, engine divergence, and the half-merge crash.

**Architecture:** One merge primitive `AccountMergeService.MergeAsync(survivor, archived, …)` runs ordered, independently-committing steps — move data (the `IUserMerge` fan-out), settle the pending email (non-fatal), then **tombstone the archived account last** as the commit point and source of truth. No cross-section `TransactionScope`. The lossy `DuplicateAccountService.ResolveAsync` is deleted; duplicate detection consolidates to one owner; the merge/duplicate services move `Profiles → Users`.

**Tech Stack:** ASP.NET Core MVC, EF Core (Npgsql), NodaTime, xUnit (`[HumansFact]`) + NSubstitute + AwesomeAssertions + `MergeFixtureBuilder` integration harness.

**Spec:** `docs/superpowers/specs/2026-06-06-account-merge-consolidation-design.md`

**Build/test:** `dotnet build Humans.slnx -v quiet` · `dotnet test Humans.slnx -v quiet` · filter: `--filter "FullyQualifiedName~<ClassName>"`

---

## Confirmed facts (do not re-derive)

- Concrete classes are `AccountMergeService` / `DuplicateAccountService` in `Humans.Application.Services.Profiles`. `ProfilesAccountMergeService` / `ProfilesDuplicateAccountService` are **`using` aliases** at `ProfileSectionExtensions.cs:9-10`.
- `IUserMerge.ReassignAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct)`. The Users impl (`UserService.ReassignAsync`) re-links external logins + event participation + sub-aggregates; `UserEmailService.ReassignAsync` → `UserRepository.ReassignUserEmailsToUserAsync`, which **collapses same-address rows onto the target and OR-combines `IsVerified`** (`UserRepository.UserEmails.cs:185-195`).
- `IUserService.AnonymizeForMergeAsync(source, target, now, ct)` tombstones the source (sets `MergedToUserId`/`MergedAt`, lockout). `UserInfo` exposes `MergedToUserId`, `MergedAt`, `IsMerged`, `IsTombstone`.
- `IAccountMergeRepository`: `GetPendingAsync()` (all pending), `GetByIdAsync`/`GetByIdPlainAsync`, `UpdateAsync`, `HasPending*`. **No pair-scoped query exists** — reconciliation filters `GetPendingAsync()` in memory (pending set is tiny at ~500 users; reuse-first, no new repo method).
- `IUserRepository.MarkUserEmailVerifiedAsync(emailId, now, ct) → bool` (false if missing) and `RemoveUserEmailByIdAsync(emailId, ct)`. Post-move `AccountMergeService` is in `Users`, so `IUserRepository` is **same-section** — these direct calls are allowed; the only change is making the missing-email case non-fatal.
- Tests: unit at `tests/Humans.Application.Tests/Services/AccountMergeServiceAdminMergeTests.cs` (NSubstitute, `BuildSut()`, `FakeClock`); integration at `tests/Humans.Integration.Tests/AccountMerge/` (`HumansWebApplicationFactory` + `factory.SeedMergeFixtureAsync(b => …)`). `[HumansFact(Timeout = …)]` always.

---

## Phase 0 — Namespace move (PETER, ReSharper) — GATE

> Not an agent task. This runs **first** so all later code is authored/edited in the `Users` home. Agents must not author the rename.

**Move (ReSharper “Move to Folder” / “Move to Namespace”, which updates all references + the two `using` aliases):**

- `src/Humans.Application/Services/Profiles/AccountMergeService.cs` → `Services/Users/` (ns `Humans.Application.Services.Users`)
- `src/Humans.Application/Services/Profiles/DuplicateAccountService.cs` → `Services/Users/`
- `src/Humans.Application/Interfaces/Profiles/IAccountMergeService.cs` → `Interfaces/Users/`
- `src/Humans.Application/Interfaces/Profiles/IDuplicateAccountService.cs` → `Interfaces/Users/`
- `src/Humans.Application/Interfaces/Repositories/IAccountMergeRepository.cs` → stays (repository interfaces are grouped under `Interfaces/Repositories`), but update its XML-doc section note to “Users”.
- `src/Humans.Infrastructure/Repositories/Profiles/AccountMergeRepository.cs` → `Infrastructure/Repositories/Users/`
- DI: move the `IAccountMergeRepository`, `ProfilesAccountMergeService`/`IAccountMergeService`/`IUserDataContributor`, and `IDuplicateAccountService` registrations from `ProfileSectionExtensions.cs` to `UsersSectionExtensions.cs`; rename the two aliases to `UsersAccountMergeService` / `UsersDuplicateAccountService` targeting the new namespace.

- [ ] **Step 1 (Peter):** Perform the moves above in ReSharper.
- [ ] **Step 2 (Peter):** Move the DI registrations + aliases `ProfileSectionExtensions → UsersSectionExtensions`.
- [ ] **Step 3:** `dotnet build Humans.slnx -v quiet` — Expected: PASS (no unresolved references).
- [ ] **Step 4:** `git commit -am "refactor: move account-merge + duplicate services to Users namespace"`.

> All paths below assume post-move locations: `Services/Users/AccountMergeService.cs`, etc.

---

## Phase 1 — Ordered merge engine

### Task 1: `MergeAsync` — the one ordered primitive

**Files:**
- Modify: `src/Humans.Application/Services/Users/AccountMergeService.cs`
- Modify: `src/Humans.Application/Interfaces/Users/IAccountMergeService.cs`
- Test: `tests/Humans.Integration.Tests/AccountMerge/MergeAsyncOrderedTests.cs` (new)

- [ ] **Step 1: Write the failing integration test** — `tests/Humans.Integration.Tests/AccountMerge/MergeAsyncOrderedTests.cs`

```csharp
using AwesomeAssertions;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Integration.Tests.AccountMerge;

public class MergeAsyncOrderedTests(HumansWebApplicationFactory factory)
    : IClassFixture<HumansWebApplicationFactory>
{
    [HumansFact(Timeout = 60_000)]
    public async Task MergeAsync_FoldsArchivedIntoSurvivor_AndTombstonesArchivedOnly()
    {
        var admin = Guid.NewGuid();
        var (survivorId, archivedId) = await factory.SeedMergeFixtureAsync(b =>
        {
            b.WithTargetEmail("keep@example.com", verified: true, isPrimary: true);   // survivor
            b.WithSourceEmail("dupe@example.com", verified: true, isPrimary: true);   // archived
            b.WithSourceRoleAssignment("Photographer");
        });

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var sut = scope.ServiceProvider.GetRequiredService<IAccountMergeService>();
            await sut.MergeAsync(survivorId, archivedId, admin);
        }

        await using var assert = factory.Services.CreateAsyncScope();
        var db = assert.ServiceProvider.GetRequiredService<HumansDbContext>();
        var survivor = await db.Users.FindAsync(survivorId);
        var archived = await db.Users.FindAsync(archivedId);
        survivor!.MergedToUserId.Should().BeNull("the survivor is never tombstoned");
        archived!.MergedToUserId.Should().Be(survivorId, "the archived account is tombstoned into the survivor");
    }
}
```

- [ ] **Step 2: Run it — expect FAIL** (`MergeAsync` does not exist)

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~MergeAsyncOrderedTests"`
Expected: FAIL (compile error: no `MergeAsync`).

- [ ] **Step 3: Add `MergeAsync` to the interface** — `IAccountMergeService.cs`

```csharp
/// <summary>
/// The one merge primitive. Folds <paramref name="archivedUserId"/> into
/// <paramref name="survivorUserId"/> via the IUserMerge fan-out, settles the
/// optional pending email, then tombstones the archived account LAST.
/// Ordered, no wrapping transaction; safely retryable. Reconciles any pending
/// merge requests for the pair.
/// </summary>
Task MergeAsync(
    Guid survivorUserId, Guid archivedUserId, Guid adminUserId,
    string? notes = null, Guid? pendingEmailIdToVerify = null,
    CancellationToken ct = default);
```

- [ ] **Step 4: Implement `MergeAsync`** in `AccountMergeService.cs` (replaces the private `FoldAsync` body — extract the ordered steps, drop the `TransactionScope`):

```csharp
public async Task MergeAsync(
    Guid survivorUserId, Guid archivedUserId, Guid adminUserId,
    string? notes = null, Guid? pendingEmailIdToVerify = null,
    CancellationToken ct = default)
{
    if (survivorUserId == archivedUserId)
        throw new InvalidOperationException("Survivor and archived users are the same.");

    var survivor = await userService.GetUserInfoAsync(survivorUserId, ct)
        ?? throw new InvalidOperationException($"Survivor user {survivorUserId} not found.");
    var archived = await userService.GetUserInfoAsync(archivedUserId, ct)
        ?? throw new InvalidOperationException($"Archived user {archivedUserId} not found.");
    if (survivor.IsMerged)
        throw new InvalidOperationException($"Survivor user {survivorUserId} is already tombstoned.");
    if (archived.IsMerged)
        throw new InvalidOperationException($"Archived user {archivedUserId} is already tombstoned (merged into {archived.MergedToUserId}).");

    var now = clock.GetCurrentInstant();

    logger.LogInformation(
        "Admin {AdminId} merging: folding {ArchivedId} into {SurvivorId}",
        adminUserId, archivedUserId, survivorUserId);

    try
    {
        // 1. Move every section's user-keyed rows archived -> survivor.
        //    Ordered/sequential; each commits independently; re-FK of already-moved rows is a no-op (retryable).
        foreach (var merger in userMerges)
            await merger.ReassignAsync(archivedUserId, survivorUserId, adminUserId, now, ct);

        // 2. Settle the pending email (gmail-normalized-but-not-identical case the row-collapse missed).
        //    NON-FATAL: a missing/already-consumed pending email is the desired end state, not an error.
        if (pendingEmailIdToVerify is Guid pendingId)
            await userRepository.MarkUserEmailVerifiedAsync(pendingId, now, ct); // ignore bool result

        // 3. Tombstone the archived account LAST — the observable commit point and source of truth.
        await userService.AnonymizeForMergeAsync(archivedUserId, survivorUserId, now, ct);

        // 4. Audit.
        await auditLogService.LogAsync(
            AuditAction.AccountMergeAccepted,
            nameof(User), archivedUserId,
            $"Folded archived {archivedUserId} into survivor {survivorUserId}. Notes: {notes ?? "(none)"}",
            adminUserId,
            relatedEntityId: survivorUserId, relatedEntityType: nameof(User));

        // 5. Best-effort: close pending merge requests for this pair. If this throws, the tombstone
        //    (step 3) already makes them self-reconcilable on the listing page — no half-merge.
        await CloseRequestsForPairAsync(survivorUserId, archivedUserId, adminUserId, now, notes, ct);
    }
    finally
    {
        InvalidateMergeCaches(survivorUserId, archivedUserId);
    }
}
```

Add the two private helpers (extract the existing cache-invalidation block from `FoldAsync` verbatim into `InvalidateMergeCaches`; `CloseRequestsForPairAsync` is new, in-memory filter of `GetPendingAsync`):

```csharp
private async Task CloseRequestsForPairAsync(
    Guid survivorUserId, Guid archivedUserId, Guid adminUserId,
    Instant now, string? notes, CancellationToken ct)
{
    var pending = await mergeRepository.GetPendingAsync(ct);
    var pair = new HashSet<Guid> { survivorUserId, archivedUserId };
    foreach (var req in pending.Where(r => pair.Contains(r.SourceUserId) && pair.Contains(r.TargetUserId)))
    {
        req.Status = AccountMergeRequestStatus.Accepted;
        req.ResolvedAt = now;
        req.ResolvedByUserId = adminUserId;
        req.AdminNotes = notes ?? "Resolved by merge.";
        await mergeRepository.UpdateAsync(req, ct);
    }
}

private void InvalidateMergeCaches(Guid survivorUserId, Guid archivedUserId)
{
    roleAssignmentService.InvalidateClaimsCacheForUser(archivedUserId);
    roleAssignmentService.InvalidateClaimsCacheForUser(survivorUserId);
    roleAssignmentService.InvalidateNavBadgeCache();
    roleAssignmentService.InvalidateRoleAssignmentCache();
    notificationService.InvalidateBadgeCachesForUsers([archivedUserId, survivorUserId]);
    consentCacheInvalidator.InvalidateUser(archivedUserId);
    consentCacheInvalidator.InvalidateUser(survivorUserId);
    activeTeamsCacheInvalidator.Invalidate();
}
```

- [ ] **Step 5: Run the test — expect PASS**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~MergeAsyncOrderedTests"`
Expected: PASS.

- [ ] **Step 6: Commit** — `git commit -am "feat(merge): add ordered MergeAsync primitive (tombstone last, no cross-section txn)"`

---

### Task 2: Reframe `AcceptAsync` + `AdminMergeAsync` onto `MergeAsync`

**Files:**
- Modify: `src/Humans.Application/Services/Users/AccountMergeService.cs`
- Modify: `src/Humans.Application/Interfaces/Users/IAccountMergeService.cs`
- Test: `tests/Humans.Integration.Tests/AccountMerge/AcceptAsyncFoldTests.cs` (extend)

- [ ] **Step 1: Write the failing test** — admin-chosen survivor can flip the request's stored direction (the obs-1 fix). Add to `AcceptAsyncFoldTests.cs`:

```csharp
[HumansFact(Timeout = 60_000)]
public async Task AcceptAsync_HonoursAdminChosenSurvivor_OverRequestDirection()
{
    var admin = Guid.NewGuid();
    // Request is created Target=newAccount (would-be survivor), Source=primary. Admin flips it.
    var (requestId, requestTargetId, requestSourceId) =
        await factory.SeedPendingMergeRequestAsync("shared@example.com");

    await using (var scope = factory.Services.CreateAsyncScope())
    {
        var sut = scope.ServiceProvider.GetRequiredService<IAccountMergeService>();
        await sut.AcceptAsync(requestId, admin, survivorUserId: requestSourceId); // flip: keep the Source
    }

    await using var assert = factory.Services.CreateAsyncScope();
    var db = assert.ServiceProvider.GetRequiredService<HumansDbContext>();
    (await db.Users.FindAsync(requestSourceId))!.MergedToUserId.Should().BeNull();
    (await db.Users.FindAsync(requestTargetId))!.MergedToUserId.Should().Be(requestSourceId);
    (await db.AccountMergeRequests.FindAsync(requestId))!.Status
        .Should().Be(AccountMergeRequestStatus.Accepted);
}
```
> If `SeedPendingMergeRequestAsync` does not exist on the fixture, add it to `MergeFixtureExtensions.cs` mirroring `SeedMergeFixtureAsync` (seed two users + a `Pending` `AccountMergeRequest` with a pending `UserEmail` on the target). Keep it minimal.

- [ ] **Step 2: Run — expect FAIL** (`AcceptAsync` has no `survivorUserId` param).

- [ ] **Step 3: Change `AcceptAsync` signature + body.** Interface:

```csharp
Task AcceptAsync(Guid requestId, Guid adminUserId, Guid survivorUserId,
    string? notes = null, CancellationToken ct = default);
```

Body (replaces the old two-transaction implementation):

```csharp
public async Task AcceptAsync(
    Guid requestId, Guid adminUserId, Guid survivorUserId,
    string? notes = null, CancellationToken ct = default)
{
    var request = await mergeRepository.GetByIdPlainAsync(requestId, ct)
        ?? throw new InvalidOperationException("Merge request not found.");
    if (request.Status != AccountMergeRequestStatus.Pending)
        throw new InvalidOperationException("Merge request is not pending.");
    if (survivorUserId != request.TargetUserId && survivorUserId != request.SourceUserId)
        throw new InvalidOperationException("Survivor must be one of the request's two accounts.");

    var archivedUserId = survivorUserId == request.TargetUserId
        ? request.SourceUserId : request.TargetUserId;

    // Only verify the request's pending email if its owner (the request target) is the survivor;
    // if the admin flipped direction, the target is being archived and its email moves via the fan-out.
    var pendingEmailToVerify = survivorUserId == request.TargetUserId
        ? request.PendingEmailId : (Guid?)null;

    await MergeAsync(survivorUserId, archivedUserId, adminUserId, notes, pendingEmailToVerify, ct);
    // MergeAsync.CloseRequestsForPairAsync closes this request (and any siblings) as Accepted.
}
```

- [ ] **Step 4: Collapse `AdminMergeAsync` into a thin forwarder** (keep it on the interface for existing callers, or delete it — see Task 6). Body:

```csharp
public Task AdminMergeAsync(
    Guid sourceUserId, Guid targetUserId, Guid adminUserId,
    string? notes = null, CancellationToken ct = default) =>
    MergeAsync(survivorUserId: targetUserId, archivedUserId: sourceUserId, adminUserId, notes, null, ct);
```

- [ ] **Step 5: Delete the now-dead `RejectAsync` two-transaction internals?** No — keep `RejectAsync` (Dismiss uses it). Verify it still compiles against the moved `IUserRepository.RemoveUserEmailByIdAsync`.

- [ ] **Step 6: Delete the old private `FoldAsync`** (its logic now lives in `MergeAsync`). Update `RejectAsync` and any references.

- [ ] **Step 7: Run all account-merge tests — expect PASS** (fix the existing `AcceptAsync(requestId, admin, notes)` call sites — there is one in `AdminMergeController`, retired in Task 5; and any unit tests in `AccountMergeServiceAdminMergeTests` that call the old signatures — update them to the new ones).

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AccountMerge"`
Expected: PASS.

- [ ] **Step 8: Commit** — `git commit -am "feat(merge): admin picks survivor; AcceptAsync/AdminMergeAsync route through MergeAsync"`

---

### Task 3: Missing-pending-email is non-fatal (obs-5 regression test)

**Files:**
- Test: `tests/Humans.Integration.Tests/AccountMerge/MergeAsyncOrderedTests.cs`

- [ ] **Step 1: Write the failing-then-passing regression test**

```csharp
[HumansFact(Timeout = 60_000)]
public async Task MergeAsync_WithAlreadyGonePendingEmail_CompletesWithoutThrowing()
{
    var admin = Guid.NewGuid();
    var (survivorId, archivedId) = await factory.SeedMergeFixtureAsync(b =>
    {
        b.WithTargetEmail("keep@example.com", verified: true, isPrimary: true);
        b.WithSourceEmail("dupe@example.com", verified: true, isPrimary: true);
    });

    await using var scope = factory.Services.CreateAsyncScope();
    var sut = scope.ServiceProvider.GetRequiredService<IAccountMergeService>();

    // A pending-email id that does not exist must NOT throw and MUST still tombstone.
    var act = async () => await sut.MergeAsync(
        survivorId, archivedId, admin, pendingEmailIdToVerify: Guid.NewGuid());

    await act.Should().NotThrowAsync();
    await using var assert = factory.Services.CreateAsyncScope();
    var db = assert.ServiceProvider.GetRequiredService<HumansDbContext>();
    (await db.Users.FindAsync(archivedId))!.MergedToUserId.Should().Be(survivorId);
}
```

- [ ] **Step 2: Run — expect PASS** (already handled by `MarkUserEmailVerifiedAsync` bool being ignored in Task 1). If it throws, the implementation still inspects the bool — fix.

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~MergeAsyncOrderedTests"`
Expected: PASS.

- [ ] **Step 3: Commit** — `git commit -am "test(merge): missing pending email is non-fatal"`

---

## Phase 2 — Detection consolidation + lossy path removal

### Task 4: Delete `ResolveAsync`; drop EmailProblems' duplicate detection

**Files:**
- Modify: `src/Humans.Application/Services/Users/DuplicateAccountService.cs`
- Modify: `src/Humans.Application/Interfaces/Users/IDuplicateAccountService.cs`
- Modify: `src/Humans.Application/Services/Profiles/EmailProblemsService.cs`
- Modify: `src/Humans.Application/Interfaces/Profiles/IEmailProblemsService.cs`
- Modify: `src/Humans.Application/DTOs/EmailProblems/EmailProblemKind.cs`
- Modify: `src/Humans.Web/Views/ProfileAdmin/EmailProblems.cshtml`
- Modify: `src/Humans.Web/Models/EmailProblems/EmailProblemsListViewModel.cs`

- [ ] **Step 1: Delete `ResolveAsync`** from `DuplicateAccountService` and `IDuplicateAccountService`. Remove the constructor params it alone used: `userRepository`, `auditLogService`, `userInfoInvalidator`, `clock`. Keep `userService`, `teamService`, `roleAssignmentService` (detection + counts). Remove now-unused `using`s (`System.Transactions`, etc.).
- [ ] **Step 2: Delete `SharedAcrossUsers` detection** from `EmailProblemsService.ScanAsync` (the cross-user normalized-email loop, ~lines 47-79) and delete `UsersShareAnyEmailAsync` (and its interface entry).
- [ ] **Step 3: Remove the `SharedAcrossUsers` enum value** from `EmailProblemKind.cs` and every reference: the `case EmailProblemKind.SharedAcrossUsers` rendering in `EmailProblems.cshtml` and `EmailProblemsListViewModel.cs`. Replace that section of the view with a static link: “Shared-email accounts are resolved on the [Account merges](…) page.” using `asp-controller="UsersAdminAccountMerges" asp-action="Index"`.
- [ ] **Step 4: Build** — `dotnet build Humans.slnx -v quiet` — Expected: PASS (fix any stragglers referencing the removed members).
- [ ] **Step 5: Run** — `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~EmailProblems | FullyQualifiedName~DuplicateAccount"` — Expected: PASS (delete/adjust tests asserting `SharedAcrossUsers` or `ResolveAsync`).
- [ ] **Step 6: Commit** — `git commit -am "refactor: delete lossy ResolveAsync; one duplicate detector (drop EmailProblems SharedAcrossUsers)"`

---

## Phase 3 — Unified surface

### Task 5: `UsersAdminAccountMergesController` + view + view models; retire the old pages

**Files:**
- Create: `src/Humans.Web/Controllers/UsersAdminAccountMergesController.cs`
- Create: `src/Humans.Web/Views/UsersAdminAccountMerges/Index.cshtml`
- Create: `src/Humans.Web/Views/UsersAdminAccountMerges/Detail.cshtml`
- Modify: `src/Humans.Web/Models/AdminViewModels.cs`
- Delete: `Controllers/AdminMergeController.cs`, `Controllers/AdminDuplicateAccountsController.cs`, `Views/AdminMerge/`, `Views/AdminDuplicateAccounts/`
- Modify: `Controllers/ProfileAdminController.cs`, delete `Views/ProfileAdmin/EmailProblemsCompare.cshtml`, `Models/EmailProblems/EmailProblemsCompareViewModel.cs`
- Modify: `src/Humans.Web/ViewComponents/AdminNavTree.cs`
- Test: `tests/Humans.Application.Tests/Controllers/UsersAdminAccountMergesControllerTests.cs` (new)

- [ ] **Step 1: Add the unified view models** to `AdminViewModels.cs` (reuse `ProfileSummaryViewModel` for cards):

```csharp
public class AccountMergeQueueViewModel
{
    public List<AccountMergeRowViewModel> Rows { get; set; } = [];
}

public class AccountMergeRowViewModel
{
    public Guid? RequestId { get; set; }                 // null = detection-only pair
    public string SharedEmail { get; set; } = string.Empty;
    public ProfileSummaryViewModel AccountA { get; set; } = new();
    public ProfileSummaryViewModel AccountB { get; set; } = new();
    public bool FromUserRequest { get; set; }            // had a pending request
    public bool AlreadyMerged { get; set; }              // one side is tombstoned -> show "Close"
    public DateTime? RequestedAt { get; set; }
}
```

- [ ] **Step 2: Write the controller** — three POST actions (`Merge` picks survivor; `Dismiss` → reject; `Close` → reconcile an already-merged orphan). Mirror `HumansControllerBase` usage (`RequireCurrentUserAsync`, `SetSuccess`/`SetError`, `[ValidateAntiForgeryToken]`).

```csharp
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Users/Admin/AccountMerges")]
public class UsersAdminAccountMergesController(
    IUserServiceRead userService,
    IAccountMergeService mergeService,
    IDuplicateAccountService duplicateService,
    ILogger<UsersAdminAccountMergesController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        // Compose: detected duplicate pairs + pending requests, cross-referenced, with already-merged flags.
        // (Build AccountMergeQueueViewModel here; read tombstone state from UserInfo.IsMerged.)
        // ...
        return View(vm);
    }

    [HttpPost("Merge")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Merge(Guid survivorUserId, Guid archivedUserId, string? notes, CancellationToken ct)
    {
        var (error, admin) = await RequireCurrentUserAsync(ct);
        if (error is not null) return error;
        try
        {
            await mergeService.MergeAsync(survivorUserId, archivedUserId, admin.Id, notes, null, ct);
            SetSuccess("Accounts merged. The archived account has been tombstoned.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Merge failed: survivor {Survivor}, archived {Archived}", survivorUserId, archivedUserId);
            SetError($"Merge failed: {ex.Message}");
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{requestId:guid}/Dismiss")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Dismiss(Guid requestId, string? notes, CancellationToken ct)
    {
        var (error, admin) = await RequireCurrentUserAsync(ct);
        if (error is not null) return error;
        try
        {
            await mergeService.RejectAsync(requestId, admin.Id, notes, ct);
            SetSuccess("Merge request dismissed.");
        }
        catch (InvalidOperationException ex) { SetError($"Dismiss failed: {ex.Message}"); }
        return RedirectToAction(nameof(Index));
    }
}
```
> For request-driven merges where the admin picks the survivor, the `Merge` form posts `survivorUserId`/`archivedUserId` derived from the chosen radio; if you prefer to keep the `requestId` linkage for the pending-email verify, add a `MergeRequest(Guid requestId, Guid survivorUserId, …)` action that calls `mergeService.AcceptAsync(requestId, admin.Id, survivorUserId, notes, ct)`. Use `AcceptAsync` for request rows (so the pending email is settled) and `MergeAsync` for detection-only rows.

- [ ] **Step 3: Author the views.** `Index.cshtml`: one table/card list of `AccountMergeRowViewModel`; each row renders both accounts via `<partial name="_ProfileCard" …>` or `<vc:human …>`, a “Merge…” control (radio to pick survivor + notes + `data-confirm`), a “Dismiss” button for request rows, and a “Close” button for `AlreadyMerged` rows. Copy the form/antiforgery/`data-confirm` patterns from the retired `AdminDuplicateAccounts/Detail.cshtml` and `AdminMerge/Detail.cshtml`.
- [ ] **Step 4: Retire old controllers/views** — delete `AdminMergeController`, `AdminDuplicateAccountsController`, `Views/AdminMerge/`, `Views/AdminDuplicateAccounts/`. Remove their dead VMs from `AdminViewModels.cs` (`AccountMerge{List,Request,Detail}`, `DuplicateAccount{List,Group,Item,Detail,EmailRow}`).
- [ ] **Step 5: Trim `ProfileAdminController`** — delete `EmailProblemsCompare`, `Merge`, `ResolveAndValidateMergePairAsync`; delete `EmailProblemsCompare.cshtml` + `EmailProblemsCompareViewModel.cs`; remove the now-unused injected services (`accountMerge`, `userEmails`, `audit`, plus `teamService`/`roleAssignmentService` if only the compare used them — verify).
- [ ] **Step 6: Collapse the nav** — in `AdminNavTree.cs` “Members” group, replace the `"Merge requests"` and `"Duplicate detection"` entries with one: `new("Account merges", "UsersAdminAccountMerges", "Index", null, null, "fa-solid fa-code-merge", PolicyNames.AdminOnly),`. Keep `"Email problems"`.
- [ ] **Step 7: Controller test** — `UsersAdminAccountMergesControllerTests.cs`: `Merge` calls `IAccountMergeService.MergeAsync` with the posted survivor/archived and sets success; on `InvalidOperationException` sets error and redirects to Index. Mock with NSubstitute per the existing `ProfileAdminControllerTests` pattern.
- [ ] **Step 8: Build + full test** — `dotnet build Humans.slnx -v quiet` then `dotnet test Humans.slnx -v quiet` — Expected: PASS. Grep for stale references to `/Admin/MergeRequests`, `/Admin/DuplicateAccounts`, `AdminMerge`, `AdminDuplicateAccounts` in views/tests/e2e and update.
- [ ] **Step 9: Commit** — `git commit -am "feat(merge): unified /Users/Admin/AccountMerges page; retire three old paths"`

---

## Phase 4 — Cleanup of half-done requests (self-reconciliation)

### Task 6: Listing recognizes already-merged requests; clean up the orphan backlog

**Files:**
- Modify: `src/Humans.Application/Services/Users/AccountMergeService.cs` (read path)
- Modify: `src/Humans.Web/Controllers/UsersAdminAccountMergesController.cs` (Index + Close)
- Test: `tests/Humans.Integration.Tests/AccountMerge/ReconcileOrphanTests.cs` (new)

- [ ] **Step 1: Failing test** — a Pending request whose archived side is already tombstoned is flagged `AlreadyMerged` and can be Closed:

```csharp
[HumansFact(Timeout = 60_000)]
public async Task PendingRequest_WithTombstonedSource_IsRecognizedAsAlreadyMerged()
{
    var admin = Guid.NewGuid();
    var (requestId, targetId, sourceId) = await factory.SeedPendingMergeRequestAsync("shared@example.com");

    // Merge the pair via the engine WITHOUT going through the request (simulates the old duplicate path).
    await using (var s = factory.Services.CreateAsyncScope())
        await s.ServiceProvider.GetRequiredService<IAccountMergeService>()
            .MergeAsync(targetId, sourceId, admin);

    await using var assert = factory.Services.CreateAsyncScope();
    var db = assert.ServiceProvider.GetRequiredService<HumansDbContext>();
    // MergeAsync's CloseRequestsForPairAsync already closed it:
    (await db.AccountMergeRequests.FindAsync(requestId))!.Status
        .Should().Be(AccountMergeRequestStatus.Accepted);
}
```
> This proves the engine no longer *creates* orphans (obs-2). The pre-existing prod backlog is handled by Step 2.

- [ ] **Step 2: Run — expect PASS** (Task 1's `CloseRequestsForPairAsync` already does this). If FAIL, fix the pair filter.
- [ ] **Step 3: Index listing — flag stragglers.** In `Index`, for each Pending request, batch `userService.GetUserInfosAsync([source, target])`; set `AlreadyMerged = source.IsMerged || target.IsMerged`. Render those rows with a single **Close** button (POST `Dismiss` is fine — it sets `Rejected`; or add a `Close` action that sets `Accepted`). This clears the existing prod backlog by a click; no data migration.
- [ ] **Step 4: Build + test** — `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~ReconcileOrphan"` — Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -am "feat(merge): orphan requests self-reconcile from tombstone; one-click cleanup"`

---

## Phase 5 — Architecture tests + finalize

### Task 7: Lock the new boundaries; full sweep

**Files:**
- Modify/Create: `tests/Humans.Application.Tests/Architecture/` (mirror `AccountDeletionArchitectureTests` pattern)

- [ ] **Step 1:** Add an architecture test asserting `IAccountMergeService` and `IDuplicateAccountService` live in `Humans.Application.Interfaces.Users` (post-move), mirroring `AccountDeletionArchitectureTests`’ namespace assertion.
- [ ] **Step 2:** Update `ProfileArchitectureTests` if it referenced the moved services.
- [ ] **Step 3:** Full build + test — `dotnet build Humans.slnx -v quiet` then `dotnet test Humans.slnx -v quiet` — Expected: PASS.
- [ ] **Step 4:** Update `docs/sections/` (Users/Profiles invariant docs) for the moved ownership and the single merge surface, if those docs enumerate it.
- [ ] **Step 5: Commit** — `git commit -am "test(arch): assert Users-section home for merge services; docs"`

---

## Self-review checklist (author)

- **Spec coverage:** ordered engine (T1), tombstone-last/no-cross-section-txn (T1), admin-picks-survivor/obs-1 (T2), non-fatal email/obs-5 (T3), delete ResolveAsync + one detector/obs-3 (T4), unified surface/obs-4 + retire 3 paths (T5), reconcile orphans/obs-2 + cleanup backlog (T6), namespace move (Phase 0), arch tests (T7). ✓ all spec sections mapped.
- **Reuse-first:** no new repo method (reconcile filters `GetPendingAsync` in memory); reuse `ProfileSummaryViewModel`, `_ProfileCard`, `HumansControllerBase`, `MergeFixtureBuilder`. ✓
- **Type consistency:** `MergeAsync(survivor, archived, admin, notes, pendingEmailIdToVerify, ct)` and `AcceptAsync(requestId, admin, survivorUserId, notes, ct)` used consistently across T1/T2/T5/T6. ✓
- **Open verification handed to executor (not placeholders):** exact post-reassign email state vs `ReassignUserEmailsToUserAsync` (asserted via end-state in T3), and whether `ProfileAdminController` still needs `teamService`/`roleAssignmentService` after the compare action is removed (T5 Step 5).
