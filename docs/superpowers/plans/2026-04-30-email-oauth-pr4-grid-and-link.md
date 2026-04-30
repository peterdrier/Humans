# PR 4 — Email Grid & Link Surface Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the user-facing surface for the email-identity decoupling sequence: profile email grid with Google service-email radio, Link/Unlink/Add/Delete operations, admin grid for site admins, GDPR export key rename, and the renames absorbed in the spec.

**Architecture:** Adds two new service methods (`SetGoogleAsync`, `UnlinkAsync`) and one consolidating method (`LinkAsync` replacing `AddOAuthEmailAsync` + `SetProviderAsync`). Rewrites `Views/Profile/Emails.cshtml` as a parameterized self/admin grid. Adds `/Profile/Admin/{userId}/Emails/...` route family gated by a new `UserEmailAuthorizationHandler`. C#-only rename of `IsNotificationTarget` → `IsPrimary` with EF column pinning. No schema changes.

**Tech Stack:** ASP.NET Core MVC, EF Core, ASP.NET Identity, NodaTime, xUnit + FluentAssertions, Razor.

**Spec:** [`docs/superpowers/specs/2026-04-30-email-oauth-pr4-grid-and-link.md`](../specs/2026-04-30-email-oauth-pr4-grid-and-link.md)

**Branch:** `email-oauth-pr4-grid-ui` (worktree at `.worktrees/email-oauth-pr4`)

---

## Hard rules in effect (non-negotiable)

- **No DB column drops.** This PR has no schema changes. If `dotnet ef migrations add` produces a non-empty migration during Task 1, STOP — the EF mapping in Task 1 is wrong; do not hand-edit a migration. `architecture_no_drops_until_prod_verified`, `architecture_dont_drop_columns_for_decoupling`.
- **No DB-enforced intra-row invariants.** Service is the contract. No `CreateIndex` for `IsGoogle` partial uniqueness or `(Provider, ProviderKey)`. `feedback_db_enforcement_minimal`.
- **No hand-edited migrations.** 100% auto-generated; no `migrationBuilder.Sql`. `architecture_no_hand_edited_migrations`.
- **No startup guards.** App must always start.
- **No concurrency tokens.** No `[ConcurrencyCheck]`, no row versioning.
- **No invented fields.** Add only the entity properties, view-model properties, and method args described below. If a helper field seems useful, ASK — don't bake it in.
- **Always pass `-v quiet`** to `dotnet build` and `dotnet test`. Never pipe through `tail`/`head`/`grep`. `feedback_dotnet_verbosity`.
- **No `--no-verify`** on any commit.
- **HEREDOC for commit messages.** Every commit message ends with `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`.
- **`gh` discipline.** Always `--repo peterdrier/Humans` or `--repo nobodies-collective/Humans`. Always `comments` + `author` in `--json`.
- **Push every 3-5 tasks.** Explicit `git push` checkpoints at Task 5, 10, 15, 20, and at the end. `feedback_push_often_during_long_runs`.

---

## File Map

### Modified — Domain
- `src/Humans.Domain/Entities/UserEmail.cs` — rename C# property `IsNotificationTarget` → `IsPrimary`. No other changes (Provider/ProviderKey/IsGoogle landed in PR 3).

### Modified — Application
- `src/Humans.Application/Interfaces/Profiles/IUserEmailService.cs` — rename `SetNotificationTargetAsync` → `SetPrimaryAsync`; rename `AddOAuthEmailAsync` and `SetProviderAsync` → consolidated `LinkAsync`; add `SetGoogleAsync`; add `UnlinkAsync`; delete `AddOAuthEmailAsync` and `SetProviderAsync`.
- `src/Humans.Application/Services/Profile/UserEmailService.cs` — implement the renames and the three new methods (`SetGoogleAsync`, `LinkAsync`, `UnlinkAsync`); add precondition guard on `DeleteEmailAsync`.
- `src/Humans.Application/Interfaces/Repositories/IUserEmailRepository.cs` — rename `SetNotificationTargetExclusiveAsync` → `SetPrimaryExclusiveAsync`; rename `RewriteOAuthEmailAsync` → `RewriteLinkedEmailAsync`; add `SetGoogleExclusiveAsync`.
- `src/Humans.Infrastructure/Repositories/Profiles/UserEmailRepository.cs` — same renames + new `SetGoogleExclusiveAsync` impl.
- `src/Humans.Infrastructure/Data/Configurations/Profiles/UserEmailConfiguration.cs` — pin `IsPrimary` to column `"IsNotificationTarget"` via `HasColumnName(...)`.
- `src/Humans.Application/Services/Profile/ProfileService.cs` — GDPR export key `"IsOAuth"` → `"IsGoogle"` (around line 869) sourced from `UserEmail.IsGoogle` column, not `Provider != null`.
- `src/Humans.Application/Services/Profile/AccountMergeService.cs` — verify the five conflict-resolution rules at parent-spec §184–190; add any missing.
- `src/Humans.Application/Interfaces/Profiles/IAccountMergeService.cs` — verify `GetPendingEmailIdsAsync(Guid userId, CancellationToken)` exists; add if missing (consumed by the email grid view-model).
- `src/Humans.Application/Authorization/UserEmail/UserEmailOperationRequirement.cs` — new `IAuthorizationRequirement`.
- `src/Humans.Application/Authorization/UserEmail/UserEmailOperations.cs` — new static class exposing `Edit` requirement instance.
- `src/Humans.Domain/AuditAction.cs` — add `UserEmailGoogleSet`, `UserEmailLinked`, `UserEmailUnlinked` enum values.

### Modified — Web
- `src/Humans.Web/Authorization/UserEmailAuthorizationHandler.cs` — new `AuthorizationHandler<UserEmailOperationRequirement, Guid>` (resource = target user id).
- `src/Humans.Web/Controllers/ProfileController.cs` — replace legacy `SetGoogleServiceEmail` with `SetGoogle`; add `Link`, `Unlink`, six admin actions; rename `SetNotificationTarget` → `SetPrimary` in lockstep; authorize each action via `UserEmailOperations.Edit`.
- `src/Humans.Web/Controllers/AccountController.cs:355` — rewrite `_userEmailService.SetProviderAsync(...)` call site to `LinkAsync(userId, provider, providerKey, email, ct)`. Verify the "user is already authenticated" branch in `ExternalLoginCallback`.
- `src/Humans.Web/Models/EmailsViewModel.cs` — add `MergePendingEmailIds`, `IsAdminContext`, `RoutePrefix`, `TargetUserId`. Field `IsNotificationTarget` (per-row) → `IsPrimary`.
- `src/Humans.Web/Views/Profile/Emails.cshtml` — full rewrite per spec §View structure.
- `src/Humans.Web/Views/Profile/AdminDetail.cshtml` — `IsNotificationTarget` bell badge label → "Primary"; add "Manage emails" link to `/Profile/Admin/{userId}/Emails`.
- `src/Humans.Web/Extensions/Sections/ProfileSectionExtensions.cs` (or whichever section extension owns Profile DI) — register `UserEmailAuthorizationHandler` as a singleton.
- `src/Humans.Web/Resources/...` — add four new resx keys; rename one. All locales (en/es/de/fr/it).

### Created — Tests
- `tests/Humans.Application.Tests/Services/Profile/UserEmailServiceTests.cs` — extend with the new method tests (file already exists; do not overwrite).
- `tests/Humans.Application.Tests/Services/Profile/AccountMergeServiceRulesTests.cs` — one test per parent-spec §184–190 rule.
- `tests/Humans.Application.Tests/Services/Profile/ProfileServiceGdprExportTests.cs` — `IsGoogle` JSON key assertion (extend if a file exists).
- `tests/Humans.Application.Tests/Architecture/UserArchitectureTests.cs` — extend (file exists).
- `tests/Humans.Web.Tests/Authorization/UserEmailAuthorizationHandlerTests.cs` — handler unit tests.
- `tests/Humans.Web.Tests/Profile/EmailGridFlowTests.cs` — cross-User merge integration + admin auth gate + admin-driven cross-User merge.
- `tests/Humans.Web.Tests/Controllers/ProfileControllerEmailGridTests.cs` — controller integration tests for self routes (Link returns ChallengeResult, etc.).

### Created
None at the migration level — this PR has zero schema changes.

---

## Task Ordering Rationale

1. **Property rename `IsNotificationTarget` → `IsPrimary`** is foundational and mechanical. Lay it down first so subsequent task code references the renamed property.
2. **Service-method rename `SetNotificationTargetAsync` → `SetPrimaryAsync`** rides on the property rename.
3. **Repo-method rename `RewriteOAuthEmailAsync` → `RewriteLinkedEmailAsync`** is a separate trivial mechanical change.
4. **`SetGoogleExclusiveAsync` repo method** sets up the storage primitive that `SetGoogleAsync` (Task 5) needs.
5. **`SetGoogleAsync` service method** — first new business method.
6. **`LinkAsync` service method** — consolidates `AddOAuthEmailAsync` + `SetProviderAsync` into one find-or-create.
7. **Rewrite `AccountController.cs:355` call site** to use `LinkAsync`. Verify the already-authenticated branch.
8. **Delete `AddOAuthEmailAsync` and `SetProviderAsync`** — both are now replaced.
9. **`UnlinkAsync` service method** — last new business method.
10. **Architecture test extension** — placed early-mid so subsequent renames have to clear it. Forbids `"OAuth"` token in `IUserEmailService` / `UserEmailRepository` method/property names. (Renumbered per the writer's instruction; lives between Unlink and DeleteEmailAsync precondition.)
11. **`DeleteEmailAsync` precondition guard** — adds the `Provider != null` → `false` precondition.
12. **`UserEmailOperationRequirement` + `UserEmailAuthorizationHandler` + `UserEmailOperations.Edit`** — search for an existing handler first; only create new if no existing one fits.
13. **`ProfileController` self routes — `SetGoogle` (replaces legacy) + `SetPrimary` rename**.
14. **`ProfileController` self routes — `Link` action**.
15. **`ProfileController` self routes — `Unlink` action**.
16. **`ProfileController` admin routes** — six parameterized actions; no `AdminLink`.
17. **GDPR export key `IsOAuth` → `IsGoogle`** in `ProfileService`.
18. **`AccountMergeService.AcceptAsync` audit** — verify §184–190 rules and add tests.
19. **Email grid view-model** — `MergePendingEmailIds`, `IsAdminContext`, `RoutePrefix`, `TargetUserId`.
20. **Rewrite `Views/Profile/Emails.cshtml`**.
21. **`Views/Profile/AdminDetail.cshtml`** — relabel + add "Manage emails" link.
22. **Localization (resx, all locales)**.
23. **Cross-User merge integration test** — `EmailGridFlowTests.cs`.
24. **Final: build + test + push + PR**.

---

## Task 1: Rename `UserEmail.IsNotificationTarget` → `IsPrimary` (Domain + EF mapping)

Pure mechanical rename. The C# property changes; the DB column name stays `"IsNotificationTarget"` per the no-DB-renames-for-decoupling rule.

**Files:**
- Modify: `src/Humans.Domain/Entities/UserEmail.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/Profiles/UserEmailConfiguration.cs`
- Modify: every call site that reads/writes `IsNotificationTarget` (use compiler errors as the to-do list)
- Modify: `tests/Humans.Domain.Tests/Entities/UserEmailTests.cs` (or wherever existing entity tests live)

- [ ] **Step 1: Write the failing column-mapping test.**

In the existing entity-test file (or a new `tests/Humans.Domain.Tests/Entities/UserEmailMappingTests.cs` if no file exists), add:

```csharp
using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Humans.Domain.Tests.Entities;

public class UserEmailMappingTests
{
    [Fact]
    public void IsPrimary_IsMappedToLegacyIsNotificationTargetColumn()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(databaseName: nameof(IsPrimary_IsMappedToLegacyIsNotificationTargetColumn))
            .Options;

        using var ctx = new HumansDbContext(options);
        var entity = ctx.Model.FindEntityType(typeof(UserEmail))!;
        var prop = entity.FindProperty(nameof(UserEmail.IsPrimary))!;

        prop.GetColumnName().Should().Be("IsNotificationTarget",
            because: "PR 4 renames the C# property but the DB column stays — see " +
                     "architecture_dont_drop_columns_for_decoupling.");
    }
}
```

- [ ] **Step 2: Run test to verify it fails.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~IsPrimary_IsMappedToLegacyIsNotificationTargetColumn"`

Expected: FAIL — either `nameof(UserEmail.IsPrimary)` doesn't compile (the property is still named `IsNotificationTarget`) or the test report shows the property doesn't exist.

- [ ] **Step 3: Rename the C# property in the Domain entity.**

In `src/Humans.Domain/Entities/UserEmail.cs`, replace the `IsNotificationTarget` property declaration with:

```csharp
/// <summary>
/// True when this row is the canonical recipient for system notifications
/// to this user. Exactly-one-true-per-UserId is service-enforced inside
/// UserEmailService — no DB partial unique index per
/// feedback_db_enforcement_minimal. Renamed from IsNotificationTarget in
/// PR 4; the DB column keeps the legacy name "IsNotificationTarget" per
/// architecture_dont_drop_columns_for_decoupling.
/// </summary>
public bool IsPrimary { get; set; }
```

- [ ] **Step 4: Pin the column name in EF.**

In `src/Humans.Infrastructure/Data/Configurations/Profiles/UserEmailConfiguration.cs`, replace the existing `Property(e => e.IsNotificationTarget)` block with:

```csharp
// PR 4: C# property renamed IsNotificationTarget → IsPrimary; DB column
// keeps the legacy name per architecture_dont_drop_columns_for_decoupling.
builder.Property(e => e.IsPrimary)
    .HasColumnName("IsNotificationTarget")
    .IsRequired();
```

- [ ] **Step 5: Sweep call sites with the compiler.**

Run: `dotnet build Humans.slnx -v quiet`

Expected: a wave of CS0117/CS1061 errors at every remaining `IsNotificationTarget` reader/writer. Replace every `e.IsNotificationTarget` with `e.IsPrimary` (services, repos, view models, views, tests, GDPR export). Iterate until the build is clean.

- [ ] **Step 6: Confirm `dotnet ef migrations add` produces an empty migration.**

Run: `dotnet ef migrations add ProbeIsPrimaryRename --project src/Humans.Infrastructure --startup-project src/Humans.Web`

Expected: the generated `Up` and `Down` method bodies are **empty** (no `RenameColumn`, no `DropColumn`, no `AddColumn`). If they aren't empty, STOP — the `HasColumnName` pin in Step 4 is wrong. Do not hand-edit the migration.

- [ ] **Step 7: Discard the probe migration.**

Run: `dotnet ef migrations remove --project src/Humans.Infrastructure --startup-project src/Humans.Web`

Expected: the three probe files (`*_ProbeIsPrimaryRename.cs`, `*.Designer.cs`, snapshot diff) are removed; snapshot reverts.

- [ ] **Step 8: Run the test to verify it passes.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~IsPrimary_IsMappedToLegacyIsNotificationTargetColumn"`

Expected: PASS.

- [ ] **Step 9: Commit.**

```bash
git add src tests
git commit -m "$(cat <<'EOF'
refactor(domain): rename UserEmail.IsNotificationTarget -> IsPrimary (column pinned)

C#-only rename per architecture_dont_drop_columns_for_decoupling. The DB
column name stays "IsNotificationTarget"; the EF mapping pins via
HasColumnName(). dotnet ef migrations add produces an empty migration.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Rename `SetNotificationTargetAsync` → `SetPrimaryAsync` (service + repo)

Mechanical name follow-up to the property rename. `IUserEmailService.SetNotificationTargetAsync` and `IUserEmailRepository.SetNotificationTargetExclusiveAsync` lose the legacy name.

**Files:**
- Modify: `src/Humans.Application/Interfaces/Profiles/IUserEmailService.cs`
- Modify: `src/Humans.Application/Services/Profile/UserEmailService.cs`
- Modify: `src/Humans.Application/Interfaces/Repositories/IUserEmailRepository.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Profiles/UserEmailRepository.cs`
- Modify: every call site (controllers, other services, tests)

- [ ] **Step 1: Rename the interface method on `IUserEmailService`.**

Find `SetNotificationTargetAsync` in `IUserEmailService`. Rename to `SetPrimaryAsync`. Keep the signature otherwise identical.

- [ ] **Step 2: Rename the impl on `UserEmailService`.**

Same rename in the implementation. The body still calls the repo (now renamed in Steps 3–4).

- [ ] **Step 3: Rename `IUserEmailRepository.SetNotificationTargetExclusiveAsync` → `SetPrimaryExclusiveAsync`.**

Same — interface method renamed; signature otherwise identical.

- [ ] **Step 4: Rename `UserEmailRepository.SetNotificationTargetExclusiveAsync` → `SetPrimaryExclusiveAsync`.**

Update the LINQ inside to use the renamed `IsPrimary` property (already true after Task 1 if Step 5 of Task 1 swept the body, but verify).

- [ ] **Step 5: Sweep call sites.**

Run: `dotnet build Humans.slnx -v quiet`

Expected: CS0117/CS1061 errors at every remaining `SetNotificationTargetAsync` / `SetNotificationTargetExclusiveAsync` caller. Update each.

- [ ] **Step 6: Run the existing service-level tests for the renamed method.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UserEmailService"`

Expected: 0 failures (existing tests follow through the rename).

- [ ] **Step 7: Commit.**

```bash
git add src tests
git commit -m "$(cat <<'EOF'
refactor(profile): rename SetNotificationTargetAsync -> SetPrimaryAsync

Service + repo rename in lockstep with the IsPrimary property rename
in PR 4 task 1. No behavior change.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Rename `RewriteOAuthEmailAsync` → `RewriteLinkedEmailAsync` (repo)

Single-call-site mechanical rename. Predicate `e.Provider != null` is unchanged; only the method name moves.

**Files:**
- Modify: `src/Humans.Application/Interfaces/Repositories/IUserEmailRepository.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Profiles/UserEmailRepository.cs`
- Modify: the single call site (likely the OAuth-callback rename-detection branch from PR 3 — locate via build error)

- [ ] **Step 1: Rename in the interface.**

In `IUserEmailRepository`, rename `RewriteOAuthEmailAsync` to `RewriteLinkedEmailAsync`. Signature unchanged.

- [ ] **Step 2: Rename in the impl.**

Same rename in `UserEmailRepository`. The body's `e.Provider != null` predicate stays.

- [ ] **Step 3: Sweep the call site.**

Run: `dotnet build Humans.slnx -v quiet`

Expected: one CS0117/CS1061 error pointing at the OAuth-callback rename-detection caller. Rename the call.

- [ ] **Step 4: Run tests.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UserEmailRepository|FullyQualifiedName~AccountControllerOAuthRename"`

Expected: 0 failures.

- [ ] **Step 5: Commit.**

```bash
git add src tests
git commit -m "$(cat <<'EOF'
refactor(profile): rename RewriteOAuthEmailAsync -> RewriteLinkedEmailAsync

Drops the "OAuth" token from the repo method name per the spec's
no-OAuth-in-method-names rule. Predicate (e.Provider != null) is
unchanged; behavior identical.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Repo — `SetGoogleExclusiveAsync`

Mirror of the renamed `SetPrimaryExclusiveAsync`: a single-transaction flip that sets `IsGoogle = true` on the target row and `IsGoogle = false` on every sibling row for the same user.

**Files:**
- Modify: `src/Humans.Application/Interfaces/Repositories/IUserEmailRepository.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Profiles/UserEmailRepository.cs`
- Modify: `tests/Humans.Application.Tests/Repositories/UserEmailRepositoryTests.cs` (or create if no file exists)

- [ ] **Step 1: Write the failing test `SetGoogleExclusiveAsync_FlipsExclusively`.**

Use the same in-memory `HumansDbContext` fixture pattern the existing repo tests use. Seed three verified `UserEmail` rows for one user — rowA, rowB, rowC. Pre-set rowA.IsGoogle = true. Call `SetGoogleExclusiveAsync(userId, rowB.Id, ct)`. Reload all three. Assert rowA.IsGoogle == false, rowB.IsGoogle == true, rowC.IsGoogle == false.

```csharp
[Fact]
public async Task SetGoogleExclusiveAsync_FlipsExclusively()
{
    using var fixture = new UserEmailRepositoryFixture();
    var userId = Guid.NewGuid();
    var rowA = await fixture.SeedVerifiedAsync(userId, "a@x.test", isGoogle: true);
    var rowB = await fixture.SeedVerifiedAsync(userId, "b@x.test", isGoogle: false);
    var rowC = await fixture.SeedVerifiedAsync(userId, "c@x.test", isGoogle: false);

    await fixture.Repo.SetGoogleExclusiveAsync(userId, rowB.Id, default);

    var reloadedA = await fixture.GetByIdAsync(rowA.Id);
    var reloadedB = await fixture.GetByIdAsync(rowB.Id);
    var reloadedC = await fixture.GetByIdAsync(rowC.Id);

    reloadedA!.IsGoogle.Should().BeFalse();
    reloadedB!.IsGoogle.Should().BeTrue();
    reloadedC!.IsGoogle.Should().BeFalse();
}
```

(Match the existing fixture name and seeding helpers; the snippet is illustrative — copy from the closest existing test in the file.)

- [ ] **Step 2: Run the test to verify it fails.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SetGoogleExclusiveAsync_FlipsExclusively"`

Expected: FAIL — method doesn't exist on `IUserEmailRepository`.

- [ ] **Step 3: Add the interface method.**

In `IUserEmailRepository`:

```csharp
/// <summary>
/// Single-transaction flip: sets <see cref="UserEmail.IsGoogle"/> = true
/// on the target row, and IsGoogle = false on every sibling row for the
/// same user. Mirrors <see cref="SetPrimaryExclusiveAsync"/>. Owner-gate
/// (userId match) is performed by the caller.
/// </summary>
Task SetGoogleExclusiveAsync(Guid userId, Guid userEmailId, CancellationToken cancellationToken = default);
```

- [ ] **Step 4: Implement in `UserEmailRepository`.**

```csharp
public async Task SetGoogleExclusiveAsync(
    Guid userId,
    Guid userEmailId,
    CancellationToken cancellationToken = default)
{
    await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

    var rows = await _dbContext.Set<UserEmail>()
        .Where(e => e.UserId == userId)
        .ToListAsync(cancellationToken);

    var now = _clock.GetCurrentInstant();
    foreach (var row in rows)
    {
        var shouldBeGoogle = row.Id == userEmailId;
        if (row.IsGoogle == shouldBeGoogle) continue;
        row.IsGoogle = shouldBeGoogle;
        row.UpdatedAt = now;
    }

    await _dbContext.SaveChangesAsync(cancellationToken);
    await tx.CommitAsync(cancellationToken);
}
```

(If `UserEmailRepository` doesn't already have `_clock` injected, copy the constructor pattern from the renamed `SetPrimaryExclusiveAsync` — this method should mirror it 1:1 in shape.)

- [ ] **Step 5: Run the test to verify it passes.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SetGoogleExclusiveAsync_FlipsExclusively"`

Expected: PASS.

- [ ] **Step 6: Commit.**

```bash
git add src tests
git commit -m "$(cat <<'EOF'
feat(profile): add UserEmailRepository.SetGoogleExclusiveAsync

Single-transaction flip mirroring SetPrimaryExclusiveAsync: target row
IsGoogle = true, sibling rows IsGoogle = false. Service-enforced
exclusivity per feedback_db_enforcement_minimal — no DB partial unique
index.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Service — `SetGoogleAsync` + `AuditAction.UserEmailGoogleSet`

Owner-gated, verified-only, exclusive flip. Invalidates FullProfile cache. Audit row with actor/subject split.

**Files:**
- Modify: `src/Humans.Domain/AuditAction.cs` (or wherever the `AuditAction` enum lives — locate via `git grep "enum AuditAction"`)
- Modify: `src/Humans.Application/Interfaces/Profiles/IUserEmailService.cs`
- Modify: `src/Humans.Application/Services/Profile/UserEmailService.cs`
- Modify: `tests/Humans.Application.Tests/Services/Profile/UserEmailServiceTests.cs`

- [ ] **Step 1: Write the failing tests.**

In `UserEmailServiceTests`:

```csharp
[Fact]
public async Task SetGoogleAsync_FlipsExclusively()
{
    using var fixture = new UserEmailServiceFixture();
    var userId = Guid.NewGuid();
    var rowA = await fixture.SeedVerifiedAsync(userId, "a@x.test", isGoogle: true);
    var rowB = await fixture.SeedVerifiedAsync(userId, "b@x.test", isGoogle: false);

    var result = await fixture.Sut.SetGoogleAsync(userId, rowB.Id, default);

    result.Should().BeTrue();
    (await fixture.GetByIdAsync(rowA.Id))!.IsGoogle.Should().BeFalse();
    (await fixture.GetByIdAsync(rowB.Id))!.IsGoogle.Should().BeTrue();
}

[Fact]
public async Task SetGoogleAsync_RejectsOtherUser()
{
    using var fixture = new UserEmailServiceFixture();
    var ownerId = Guid.NewGuid();
    var otherId = Guid.NewGuid();
    var row = await fixture.SeedVerifiedAsync(ownerId, "a@x.test", isGoogle: false);

    var result = await fixture.Sut.SetGoogleAsync(otherId, row.Id, default);

    result.Should().BeFalse();
    (await fixture.GetByIdAsync(row.Id))!.IsGoogle.Should().BeFalse();
}

[Fact]
public async Task SetGoogleAsync_RejectsUnverified()
{
    using var fixture = new UserEmailServiceFixture();
    var userId = Guid.NewGuid();
    var row = await fixture.SeedAsync(userId, "a@x.test", isVerified: false, isGoogle: false);

    var result = await fixture.Sut.SetGoogleAsync(userId, row.Id, default);

    result.Should().BeFalse();
    (await fixture.GetByIdAsync(row.Id))!.IsGoogle.Should().BeFalse();
}

[Fact]
public async Task SetGoogleAsync_InvalidatesFullProfileCache()
{
    using var fixture = new UserEmailServiceFixture();
    var userId = Guid.NewGuid();
    var row = await fixture.SeedVerifiedAsync(userId, "a@x.test", isGoogle: false);

    await fixture.Sut.SetGoogleAsync(userId, row.Id, default);

    await fixture.FullProfileInvalidator.Received(1).InvalidateAsync(userId, Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Run tests to verify they fail.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SetGoogleAsync"`

Expected: FAIL — method doesn't exist.

- [ ] **Step 3: Add the audit action enum value.**

In `src/Humans.Domain/AuditAction.cs` (locate via `git grep "enum AuditAction"`), append:

```csharp
UserEmailGoogleSet,
```

- [ ] **Step 4: Add the interface method.**

In `IUserEmailService`:

```csharp
/// <summary>
/// Sets the user's canonical Google Workspace identity to the given verified
/// email row. Single-transaction exclusive flip via
/// <see cref="IUserEmailRepository.SetGoogleExclusiveAsync"/>. Owner-gated
/// via <c>GetByIdAndUserIdAsync</c>; returns false if the row is not found
/// for this user or is not verified. Service-auth-free per the design rules:
/// the caller (controller) authorizes against <paramref name="userId"/>;
/// <paramref name="userId"/> is the <b>target</b> user, not the actor.
/// </summary>
Task<bool> SetGoogleAsync(Guid userId, Guid userEmailId, CancellationToken cancellationToken = default);
```

- [ ] **Step 5: Implement in `UserEmailService`.**

```csharp
public async Task<bool> SetGoogleAsync(
    Guid userId,
    Guid userEmailId,
    CancellationToken cancellationToken = default)
{
    var row = await _repo.GetByIdAndUserIdAsync(userEmailId, userId, cancellationToken);
    if (row is null || !row.IsVerified) return false;

    var previousGoogle = (await _repo.GetByUserIdAsync(userId, cancellationToken))
        .FirstOrDefault(e => e.IsGoogle && e.Id != row.Id);

    await _repo.SetGoogleExclusiveAsync(userId, row.Id, cancellationToken);
    await _fullProfileInvalidator.InvalidateAsync(userId, cancellationToken);

    await _auditLog.WriteAsync(
        action: AuditAction.UserEmailGoogleSet,
        subjectUserId: userId,
        actorUserId: _currentUserAccessor.GetUserId(),
        details: new
        {
            EmailId = row.Id,
            Email = row.Email,
            PreviousGoogleEmail = previousGoogle?.Email,
        },
        cancellationToken);

    return true;
}
```

(Match the project's existing audit-log call pattern — verify `_auditLog.WriteAsync` parameter names against existing `UserEmailService` calls and against the `audit-log: attribute shift signup entries to actor and subject` commit (`7bde7a96`). If `_currentUserAccessor` doesn't exist, locate the project's existing actor accessor — likely a service that wraps `IHttpContextAccessor`.)

- [ ] **Step 6: Run tests to verify they pass.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SetGoogleAsync"`

Expected: 4 passed.

- [ ] **Step 7: Commit & push (cumulative push checkpoint — Tasks 1–5).**

```bash
git add src tests
git commit -m "$(cat <<'EOF'
feat(profile): add UserEmailService.SetGoogleAsync

Owner-gated, verified-only, exclusive flip. Invalidates FullProfile
cache. Audit-logged with actor/subject split per the
audit-log-attribution pattern. Replaces the legacy
IUserService.SetGoogleEmailAsync as the user-facing entry point —
controller call site lands later in the plan.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
git push -u origin email-oauth-pr4-grid-ui
```

---

## Task 6: Service — `LinkAsync` + `AuditAction.UserEmailLinked`

Find-or-create: locate an existing `UserEmail` for `userId` matching `email` (normalized, OrdinalIgnoreCase); if found, set `Provider`/`ProviderKey`; if not, create a new row with `IsVerified = true`, `IsPrimary = false`, `Provider`/`ProviderKey` set. Replaces the two methods deleted in Task 8.

**Files:**
- Modify: `src/Humans.Domain/AuditAction.cs`
- Modify: `src/Humans.Application/Interfaces/Profiles/IUserEmailService.cs`
- Modify: `src/Humans.Application/Services/Profile/UserEmailService.cs`
- Modify: `tests/Humans.Application.Tests/Services/Profile/UserEmailServiceTests.cs`

- [ ] **Step 1: Write the failing tests.**

```csharp
[Fact]
public async Task LinkAsync_AttachesToExistingEmail()
{
    using var fixture = new UserEmailServiceFixture();
    var userId = Guid.NewGuid();
    var existing = await fixture.SeedVerifiedAsync(userId, "x@google.test");

    var result = await fixture.Sut.LinkAsync(userId, "Google", "sub-X", "x@google.test", default);

    result.Should().BeTrue();
    var reloaded = await fixture.GetByIdAsync(existing.Id);
    reloaded!.Provider.Should().Be("Google");
    reloaded.ProviderKey.Should().Be("sub-X");
}

[Fact]
public async Task LinkAsync_CreatesRowWhenMissing()
{
    using var fixture = new UserEmailServiceFixture();
    var userId = Guid.NewGuid();

    var result = await fixture.Sut.LinkAsync(userId, "Google", "sub-Y", "y@google.test", default);

    result.Should().BeTrue();
    var rows = await fixture.GetByUserIdAsync(userId);
    var created = rows.Single(r => string.Equals(r.Email, "y@google.test", StringComparison.OrdinalIgnoreCase));
    created.IsVerified.Should().BeTrue();
    created.IsPrimary.Should().BeFalse();
    created.Provider.Should().Be("Google");
    created.ProviderKey.Should().Be("sub-Y");
}
```

- [ ] **Step 2: Run tests to verify they fail.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~LinkAsync_"`

Expected: FAIL — method doesn't exist.

- [ ] **Step 3: Add the audit action enum value.**

In `AuditAction`:

```csharp
UserEmailLinked,
```

- [ ] **Step 4: Add the interface method.**

```csharp
/// <summary>
/// Find-or-create. Attaches the OAuth identity (<paramref name="provider"/>,
/// <paramref name="providerKey"/>) to the user's email row matching
/// <paramref name="email"/> (Ordinal/case-insensitive); creates a new
/// verified row when none matches. <paramref name="userId"/> is the
/// <b>target</b> user. Replaces both AddOAuthEmailAsync and SetProviderAsync
/// (PR 4 consolidation).
/// </summary>
Task<bool> LinkAsync(
    Guid userId,
    string provider,
    string providerKey,
    string email,
    CancellationToken cancellationToken = default);
```

- [ ] **Step 5: Implement.**

```csharp
public async Task<bool> LinkAsync(
    Guid userId,
    string provider,
    string providerKey,
    string email,
    CancellationToken cancellationToken = default)
{
    var rows = await _repo.GetByUserIdAsync(userId, cancellationToken);
    var match = rows.FirstOrDefault(r =>
        string.Equals(r.Email, email, StringComparison.OrdinalIgnoreCase));

    var now = _clock.GetCurrentInstant();
    if (match is null)
    {
        match = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = email,
            IsVerified = true,
            IsPrimary = false,
            IsGoogle = false,
            Provider = provider,
            ProviderKey = providerKey,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _repo.AddAsync(match, cancellationToken);
    }
    else
    {
        match.Provider = provider;
        match.ProviderKey = providerKey;
        match.UpdatedAt = now;
        await _repo.UpdateAsync(match, cancellationToken);
    }

    await _fullProfileInvalidator.InvalidateAsync(userId, cancellationToken);
    await _auditLog.WriteAsync(
        action: AuditAction.UserEmailLinked,
        subjectUserId: userId,
        actorUserId: _currentUserAccessor.GetUserId(),
        details: new { Provider = provider, Email = email },
        cancellationToken);

    return true;
}
```

(Match the existing repo's `AddAsync` / `UpdateAsync` shape; if it differs, adapt.)

- [ ] **Step 6: Run tests to verify they pass.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~LinkAsync_"`

Expected: 2 passed.

- [ ] **Step 7: Commit.**

```bash
git add src tests
git commit -m "$(cat <<'EOF'
feat(profile): add UserEmailService.LinkAsync (consolidates AddOAuthEmail + SetProvider)

Find-or-create. Attaches Provider/ProviderKey to the user's matching
email row; creates a verified row when none matches. Replaces both
AddOAuthEmailAsync and SetProviderAsync (deletions land in Task 8 once
the AccountController call site is rewritten).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Rewrite `AccountController.cs:355` from `SetProviderAsync` to `LinkAsync`

The OAuth-callback site currently calls `_userEmailService.SetProviderAsync(matchedRow.Id, info.LoginProvider, info.ProviderKey, ct)`. PR 4 changes that to `LinkAsync(userId, provider, providerKey, email, ct)` with the email pulled from the principal claims. Also verify the "user is already authenticated" branch handles the link-while-signed-in case.

**Files:**
- Modify: `src/Humans.Web/Controllers/AccountController.cs` (around line 355 — verify with `git grep -n "SetProviderAsync" src/Humans.Web/Controllers/AccountController.cs`)
- Modify: `tests/Humans.Web.Tests/Controllers/AccountController*.cs` (existing OAuth tests)

- [ ] **Step 1: Locate the call site.**

Run: `grep -n "SetProviderAsync" src/Humans.Web/Controllers/AccountController.cs`

Expected: one or more line numbers (PR 3 wired three branches at success / link-by-email / new-user). Each becomes a `LinkAsync` call.

- [ ] **Step 2: Write a failing integration test for the link-while-signed-in branch.**

In `tests/Humans.Web.Tests/Controllers/`:

```csharp
[Fact]
public async Task ExternalLoginCallback_AlreadyAuthenticated_AttachesGoogleIdentityToCurrentUser()
{
    // Arrange: signed-in User A with one verified UserEmail (no Provider).
    // ExternalLoginInfo has LoginProvider="Google", ProviderKey="sub-NEW",
    // claim email "secondary@google.test" (a NEW email not yet on User A).

    // Act: invoke ExternalLoginCallback.

    // Assert:
    //   - User A still signed in (no new User created).
    //   - User A now has a UserEmail row with Email="secondary@google.test",
    //     Provider="Google", ProviderKey="sub-NEW", IsVerified=true.
    //   - No second User created.
}
```

(Use the project's existing controller-test fixture pattern — match `AccountControllerOAuthRenameDetectionTests` from PR 3.)

- [ ] **Step 3: Run test to verify it fails.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~ExternalLoginCallback_AlreadyAuthenticated"`

Expected: FAIL — either the branch doesn't exist or the call site is still `SetProviderAsync`.

- [ ] **Step 4: Rewrite the call site(s).**

For each `SetProviderAsync` call site, replace with:

```csharp
var claimEmail = info.Principal.FindFirstValue(ClaimTypes.Email);
if (!string.IsNullOrWhiteSpace(claimEmail))
{
    await _userEmailService.LinkAsync(
        targetUser.Id,
        info.LoginProvider,
        info.ProviderKey,
        claimEmail,
        ct);
}
```

- [ ] **Step 5: Verify the already-authenticated branch.**

Read the current `ExternalLoginCallback` body. If there is no branch that handles the "user is already signed in" case (i.e. `User.Identity?.IsAuthenticated == true` at entry), add one early in the action that:

1. Resolves the current user via `await _userManager.GetUserAsync(User)`.
2. Calls `LinkAsync(currentUser.Id, info.LoginProvider, info.ProviderKey, claimEmail, ct)`.
3. Redirects back to `returnUrl` or `/Profile/Me/Emails`.

If the branch already exists, ensure it routes through `LinkAsync` rather than the old `AddLoginAsync` + `SetProviderAsync` pair.

- [ ] **Step 6: Run tests to verify they pass.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~ExternalLoginCallback"`

Expected: 0 failures (existing PR 3 tests + the new already-authenticated test).

- [ ] **Step 7: Commit.**

```bash
git add src tests
git commit -m "$(cat <<'EOF'
refactor(auth): rewrite OAuth callback to use LinkAsync

Replaces the SetProviderAsync call site at AccountController.cs:355
with the consolidated LinkAsync(userId, provider, providerKey, email).
Adds the link-while-signed-in branch (or verifies it routes through
LinkAsync) so Profile/Me/Emails -> Link Google account works.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: Delete `AddOAuthEmailAsync` and `SetProviderAsync`

Both are now replaced. Delete from the interface, impl, and any remaining callers.

**Files:**
- Modify: `src/Humans.Application/Interfaces/Profiles/IUserEmailService.cs`
- Modify: `src/Humans.Application/Services/Profile/UserEmailService.cs`
- Modify: any remaining callers (compiler will surface them)
- Delete from: `tests/Humans.Application.Tests/Services/Profile/UserEmailServiceTests.cs` and any related test files (the tests for the deleted methods).

- [ ] **Step 1: Delete the interface methods.**

In `IUserEmailService`, remove `AddOAuthEmailAsync` and `SetProviderAsync` declarations.

- [ ] **Step 2: Delete the impl methods.**

In `UserEmailService`, remove the two method bodies.

- [ ] **Step 3: Sweep callers.**

Run: `dotnet build Humans.slnx -v quiet`

Expected: any remaining caller of either method becomes a CS1061 error. Update each to `LinkAsync` (should be zero after Task 7's sweep — this step is a safety net).

- [ ] **Step 4: Delete the corresponding tests.**

Find tests named `AddOAuthEmailAsync_*` and `SetProviderAsync_*` in `UserEmailServiceTests.cs` and delete them. The behavior is now covered by `LinkAsync_AttachesToExistingEmail` and `LinkAsync_CreatesRowWhenMissing`.

- [ ] **Step 5: Run tests.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UserEmailService"`

Expected: 0 failures.

- [ ] **Step 6: Commit.**

```bash
git add src tests
git commit -m "$(cat <<'EOF'
refactor(profile): delete AddOAuthEmailAsync and SetProviderAsync

Both replaced by LinkAsync (PR 4 task 6). Callers swept in task 7;
this commit removes the deprecated surface.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: Service — `UnlinkAsync` + `AuditAction.UserEmailUnlinked`

Owner-gated. Pre-condition `Provider != null && ProviderKey != null` (returns false otherwise). Calls `UserManager.RemoveLoginAsync` to drop `AspNetUserLogins`. Removes the email row entirely. Invalidates FullProfile cache. Audit row with provider, hashed providerKey, email.

**Files:**
- Modify: `src/Humans.Domain/AuditAction.cs`
- Modify: `src/Humans.Application/Interfaces/Profiles/IUserEmailService.cs`
- Modify: `src/Humans.Application/Services/Profile/UserEmailService.cs`
- Modify: `tests/Humans.Application.Tests/Services/Profile/UserEmailServiceTests.cs`

- [ ] **Step 1: Write the failing tests.**

```csharp
[Fact]
public async Task UnlinkAsync_RemovesAspNetUserLoginsAndEmailRow()
{
    using var fixture = new UserEmailServiceFixture();
    var userId = Guid.NewGuid();
    var row = await fixture.SeedVerifiedAsync(
        userId, "linked@google.test",
        provider: "Google", providerKey: "sub-Z");
    fixture.SeedAspNetUserLogin(userId, "Google", "sub-Z");

    var result = await fixture.Sut.UnlinkAsync(userId, row.Id, default);

    result.Should().BeTrue();
    (await fixture.GetByIdAsync(row.Id)).Should().BeNull();
    fixture.GetAspNetUserLogin(userId, "Google", "sub-Z").Should().BeNull();
}

[Fact]
public async Task UnlinkAsync_RejectsRowWithoutProvider()
{
    using var fixture = new UserEmailServiceFixture();
    var userId = Guid.NewGuid();
    var row = await fixture.SeedVerifiedAsync(userId, "plain@x.test");

    var result = await fixture.Sut.UnlinkAsync(userId, row.Id, default);

    result.Should().BeFalse();
    (await fixture.GetByIdAsync(row.Id)).Should().NotBeNull();
}
```

- [ ] **Step 2: Run tests to verify they fail.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UnlinkAsync_"`

Expected: FAIL — method doesn't exist.

- [ ] **Step 3: Add the audit action enum value.**

In `AuditAction`:

```csharp
UserEmailUnlinked,
```

- [ ] **Step 4: Add the interface method.**

```csharp
/// <summary>
/// Removes both the AspNetUserLogins row and the UserEmail row for a
/// Provider-attached email. Owner-gated. Returns false if the row is
/// not found for this user or has no Provider/ProviderKey.
/// <paramref name="userId"/> is the <b>target</b> user.
/// </summary>
Task<bool> UnlinkAsync(Guid userId, Guid userEmailId, CancellationToken cancellationToken = default);
```

- [ ] **Step 5: Implement.**

```csharp
public async Task<bool> UnlinkAsync(
    Guid userId,
    Guid userEmailId,
    CancellationToken cancellationToken = default)
{
    var row = await _repo.GetByIdAndUserIdAsync(userEmailId, userId, cancellationToken);
    if (row is null) return false;
    if (string.IsNullOrEmpty(row.Provider) || string.IsNullOrEmpty(row.ProviderKey))
        return false;

    var user = await _userManager.FindByIdAsync(userId.ToString());
    if (user is null) return false;

    var removeLogin = await _userManager.RemoveLoginAsync(user, row.Provider, row.ProviderKey);
    if (!removeLogin.Succeeded)
    {
        _logger.LogWarning(
            "UnlinkAsync: RemoveLoginAsync failed for user {UserId}, provider {Provider}: {Errors}",
            userId, row.Provider, string.Join(",", removeLogin.Errors.Select(e => e.Code)));
        // Continue: per spec, no "you'd lock yourself out" guard — magic-link is the fallback.
    }

    await _repo.DeleteAsync(row.Id, cancellationToken);
    await _fullProfileInvalidator.InvalidateAsync(userId, cancellationToken);

    await _auditLog.WriteAsync(
        action: AuditAction.UserEmailUnlinked,
        subjectUserId: userId,
        actorUserId: _currentUserAccessor.GetUserId(),
        details: new
        {
            Provider = row.Provider,
            ProviderKeyHash = ShortHash(row.ProviderKey),
            Email = row.Email,
        },
        cancellationToken);

    return true;
}

private static string ShortHash(string s)
{
    var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
    return Convert.ToHexString(bytes.AsSpan(0, 8));
}
```

(If a `ShortHash` helper already exists in the codebase, reuse it instead.)

- [ ] **Step 6: Run tests to verify they pass.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UnlinkAsync_"`

Expected: 2 passed.

- [ ] **Step 7: Commit.**

```bash
git add src tests
git commit -m "$(cat <<'EOF'
feat(profile): add UserEmailService.UnlinkAsync

Removes both AspNetUserLogins and the UserEmail row for a Provider-
attached email. Owner-gated; precondition Provider/ProviderKey non-null.
No "lock yourself out" guard — magic-link is the fallback. Audit-logged
with hashed providerKey.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: Architecture test — forbid `"OAuth"` token in `IUserEmailService` / `UserEmailRepository`

Placed early-mid so the rest of the plan is constrained by it. Extends `UserArchitectureTests.cs` to fail when any method or property name on `IUserEmailService` or `UserEmailRepository` contains the case-insensitive token `"OAuth"`. String literals and comments are allowed.

**Files:**
- Modify: `tests/Humans.Application.Tests/Architecture/UserArchitectureTests.cs`

- [ ] **Step 1: Write the failing assertion.**

Append to `UserArchitectureTests`:

```csharp
[HumansFact]
public void NoOAuthTokenInUserEmailServiceOrRepositoryMethodNames()
{
    var offenders = new List<string>();

    var typesToScan = new[]
    {
        typeof(Humans.Application.Interfaces.Profiles.IUserEmailService),
        // Repo impl lives in Infrastructure; resolve at runtime.
        Type.GetType("Humans.Infrastructure.Repositories.Profiles.UserEmailRepository, Humans.Infrastructure"),
        typeof(Humans.Application.Interfaces.Repositories.IUserEmailRepository),
    }.Where(t => t is not null);

    foreach (var t in typesToScan)
    {
        foreach (var m in t!.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (m.Name.Contains("OAuth", StringComparison.OrdinalIgnoreCase))
                offenders.Add($"{t.Name}.{m.Name} (method)");
        }
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (p.Name.Contains("OAuth", StringComparison.OrdinalIgnoreCase))
                offenders.Add($"{t.Name}.{p.Name} (property)");
        }
    }

    offenders.Should().BeEmpty(
        because: "PR 4 of the email-identity-decoupling spec drops the 'OAuth' token from " +
                 "IUserEmailService / UserEmailRepository method/property names. " +
                 "Provider-specific operations are parameterized via a Provider arg. " +
                 "Offenders: {0}",
        string.Join("; ", offenders));
}
```

- [ ] **Step 2: Run the test.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~NoOAuthTokenInUserEmailServiceOrRepositoryMethodNames"`

Expected outcome:
- If Tasks 2–8 fully cleaned the surface, **PASS** (architectural ratchet captured).
- If anything `OAuth`-named slipped through, **FAIL** with the offender list — go fix each before continuing.

- [ ] **Step 3: Commit.**

```bash
git add tests
git commit -m "$(cat <<'EOF'
test(architecture): forbid OAuth token in IUserEmailService/UserEmailRepository names

Locks in the rename ratchet so future regressions on Provider-parameterized
naming fail at the architecture-test layer.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: `DeleteEmailAsync` precondition guard

A service-method update on `UserEmailService.DeleteEmailAsync` (NOT a C# extension method). Adds a pre-delete read of the row; if `Provider != null`, returns `false`. The per-row UI never routes a Provider-attached row through Delete; this is the service-level guard.

**Files:**
- Modify: `src/Humans.Application/Services/Profile/UserEmailService.cs` (the existing `DeleteEmailAsync` body)
- Modify: `tests/Humans.Application.Tests/Services/Profile/UserEmailServiceTests.cs`

- [ ] **Step 1: Write the failing test.**

```csharp
[Fact]
public async Task DeleteEmailAsync_RejectsProviderAttachedRow()
{
    using var fixture = new UserEmailServiceFixture();
    var userId = Guid.NewGuid();
    var row = await fixture.SeedVerifiedAsync(
        userId, "linked@google.test",
        provider: "Google", providerKey: "sub-Z");

    var result = await fixture.Sut.DeleteEmailAsync(userId, row.Id, default);

    result.Should().BeFalse();
    (await fixture.GetByIdAsync(row.Id)).Should().NotBeNull();
}
```

- [ ] **Step 2: Run test to verify it fails.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~DeleteEmailAsync_RejectsProviderAttachedRow"`

Expected: FAIL — current `DeleteEmailAsync` body deletes the row regardless.

- [ ] **Step 3: Add the precondition.**

At the top of `UserEmailService.DeleteEmailAsync` (after the existing owner-gated read of the row):

```csharp
var row = await _repo.GetByIdAndUserIdAsync(userEmailId, userId, cancellationToken);
if (row is null) return false;
if (!string.IsNullOrEmpty(row.Provider))
{
    // Provider-attached rows go through UnlinkAsync (which removes the
    // AspNetUserLogins row and the email row). The per-row UI never
    // routes a Provider-attached row to Delete; this is the service-level
    // guard for non-UI callers.
    return false;
}
// ... existing delete logic ...
```

(If `DeleteEmailAsync`'s current body already does the owner-gated read, fold the new check inline instead of duplicating the read.)

- [ ] **Step 4: Run tests.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~DeleteEmailAsync"`

Expected: existing tests still pass; new precondition test passes.

- [ ] **Step 5: Commit.**

```bash
git add src tests
git commit -m "$(cat <<'EOF'
feat(profile): DeleteEmailAsync precondition — reject Provider-attached rows

Service-level guard: Provider-attached rows must go through UnlinkAsync.
The per-row UI ensures this at the form layer; the service guards
against non-UI callers.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 12: Authorization — `UserEmailOperationRequirement` + handler + `UserEmailOperations.Edit`

New resource-based handler for `Guid targetUserId`. Succeeds when actor is the target OR actor is in `Admin` role. Wire into DI. Match the existing `*OperationRequirement` + `*AuthorizationHandler` + `*Operations` pattern.

**Important:** before creating, run `git grep -l "ProfileAuthorizationHandler\|UserAuthorizationHandler"` to check for an existing handler with matching semantics. If one exists and covers per-user-edit authorization for emails, reuse it and rename to `UserEmailOperations.Edit` if necessary; do not multiply requirements.

**Files (assuming no reuse):**
- Create: `src/Humans.Application/Authorization/UserEmail/UserEmailOperationRequirement.cs`
- Create: `src/Humans.Application/Authorization/UserEmail/UserEmailOperations.cs`
- Create: `src/Humans.Web/Authorization/UserEmailAuthorizationHandler.cs`
- Modify: `src/Humans.Web/Extensions/Sections/ProfileSectionExtensions.cs` (or whichever section extension owns Profile DI)
- Create: `tests/Humans.Web.Tests/Authorization/UserEmailAuthorizationHandlerTests.cs`

- [ ] **Step 1: Search for an existing handler to reuse.**

Run: `grep -rn "AuthorizationHandler<.*Requirement" src/Humans.Web/Authorization/ src/Humans.Application/`

If a `ProfileAuthorizationHandler` or similar already exposes a per-user-edit operation taking `Guid targetUserId`, jump to Step 7 and wire the controller to that handler instead of creating new types. Otherwise continue.

- [ ] **Step 2: Write the failing handler tests.**

```csharp
public class UserEmailAuthorizationHandlerTests
{
    [Fact]
    public async Task SucceedsWhenActorIsTarget()
    {
        var sut = new UserEmailAuthorizationHandler();
        var userId = Guid.NewGuid();
        var ctx = BuildContext(actorUserId: userId, targetUserId: userId, isAdmin: false);

        await sut.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task SucceedsWhenActorIsAdmin()
    {
        var sut = new UserEmailAuthorizationHandler();
        var ctx = BuildContext(actorUserId: Guid.NewGuid(), targetUserId: Guid.NewGuid(), isAdmin: true);

        await sut.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task FailsWhenActorIsNeitherTargetNorAdmin()
    {
        var sut = new UserEmailAuthorizationHandler();
        var ctx = BuildContext(actorUserId: Guid.NewGuid(), targetUserId: Guid.NewGuid(), isAdmin: false);

        await sut.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse();
    }

    private static AuthorizationHandlerContext BuildContext(Guid actorUserId, Guid targetUserId, bool isAdmin)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actorUserId.ToString()) };
        if (isAdmin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        var requirement = UserEmailOperations.Edit;
        return new AuthorizationHandlerContext(new[] { requirement }, principal, targetUserId);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UserEmailAuthorizationHandlerTests"`

Expected: FAIL — types don't exist.

- [ ] **Step 4: Create the requirement.**

`src/Humans.Application/Authorization/UserEmail/UserEmailOperationRequirement.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;

namespace Humans.Application.Authorization.UserEmail;

public sealed class UserEmailOperationRequirement : IAuthorizationRequirement
{
    public string Name { get; }

    public UserEmailOperationRequirement(string name)
    {
        Name = name;
    }
}
```

- [ ] **Step 5: Create `UserEmailOperations`.**

```csharp
namespace Humans.Application.Authorization.UserEmail;

public static class UserEmailOperations
{
    public static readonly UserEmailOperationRequirement Edit = new(nameof(Edit));
}
```

- [ ] **Step 6: Create the handler.**

`src/Humans.Web/Authorization/UserEmailAuthorizationHandler.cs`:

```csharp
using System.Security.Claims;
using Humans.Application.Authorization.UserEmail;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization;

public sealed class UserEmailAuthorizationHandler
    : AuthorizationHandler<UserEmailOperationRequirement, Guid>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        UserEmailOperationRequirement requirement,
        Guid targetUserId)
    {
        var actorIdRaw = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(actorIdRaw, out var actorId) && actorId == targetUserId)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.User.IsInRole("Admin"))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
```

- [ ] **Step 7: Register in DI.**

In the section extension that owns Profile (search `ProfileSectionExtensions` or `services.AddScoped<IUserEmailService`):

```csharp
services.AddSingleton<IAuthorizationHandler, UserEmailAuthorizationHandler>();
```

- [ ] **Step 8: Run handler tests.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UserEmailAuthorizationHandlerTests"`

Expected: 3 passed.

- [ ] **Step 9: Commit.**

```bash
git add src tests
git commit -m "$(cat <<'EOF'
feat(authz): add UserEmailAuthorizationHandler (resource = target user id)

Self-or-admin gate. Self/admin distinction lives in one handler so the
self routes and the admin routes share authorization. Service signatures
stay auth-free per design-rules.md.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 13: Controller — self route `SetGoogle` (replaces legacy) + `SetPrimary` rename

Delete the legacy `SetGoogleServiceEmail` action and its `IUserService.SetGoogleEmailAsync` call site. Add `SetGoogle(Guid emailId)` calling `_userEmailService.SetGoogleAsync`. Authorize via `_authz.AuthorizeAsync(User, currentUserId, UserEmailOperations.Edit)`. Rename existing `SetNotificationTarget` action to `SetPrimary`.

**Files:**
- Modify: `src/Humans.Web/Controllers/ProfileController.cs`
- Create: `tests/Humans.Web.Tests/Controllers/ProfileControllerEmailGridTests.cs`

- [ ] **Step 1: Write the failing controller tests.**

```csharp
[Fact]
public async Task SetGoogle_AsSelf_CallsSetGoogleAsync_AndRedirectsToGrid()
{
    var fixture = new ProfileControllerFixture(actorUserId: SeedUserId);
    var emailId = Guid.NewGuid();

    var result = await fixture.Sut.SetGoogle(emailId);

    await fixture.UserEmailService.Received(1)
        .SetGoogleAsync(SeedUserId, emailId, Arg.Any<CancellationToken>());
    result.Should().BeOfType<RedirectToActionResult>()
        .Which.ActionName.Should().Be("Emails");
}
```

- [ ] **Step 2: Run test to verify it fails.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SetGoogle_AsSelf"`

Expected: FAIL — action doesn't exist or still routes to legacy method.

- [ ] **Step 3: Delete the legacy action.**

In `ProfileController.cs`, remove the `SetGoogleServiceEmail` action and any usage of `_userService.SetGoogleEmailAsync`. (`IUserService.SetGoogleEmailAsync` itself stays — touches `User.GoogleEmail` shadow column, deletion gated on PR 7.)

- [ ] **Step 4: Add the new self action.**

```csharp
[HttpPost("Profile/Me/Emails/SetGoogle")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SetGoogle(Guid emailId, CancellationToken ct)
{
    var userId = User.GetUserId();
    var authz = await _authz.AuthorizeAsync(User, userId, UserEmailOperations.Edit);
    if (!authz.Succeeded) return Forbid();

    await _userEmailService.SetGoogleAsync(userId, emailId, ct);
    return RedirectToAction(nameof(Emails));
}
```

- [ ] **Step 5: Rename `SetNotificationTarget` → `SetPrimary` in the controller.**

Find the existing `SetNotificationTarget` action; rename it to `SetPrimary`; the body now calls `_userEmailService.SetPrimaryAsync`. Update the route attribute if it's `SetNotificationTarget` to `SetPrimary`.

- [ ] **Step 6: Run tests.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~ProfileController" -v quiet`

Expected: 0 failures.

- [ ] **Step 7: Commit.**

```bash
git add src tests
git commit -m "$(cat <<'EOF'
feat(profile): self-grid SetGoogle + SetPrimary controller actions

Replaces the legacy SetGoogleServiceEmail action and its
IUserService.SetGoogleEmailAsync call site with SetGoogle ->
_userEmailService.SetGoogleAsync. Renames SetNotificationTarget ->
SetPrimary in lockstep with the IsPrimary property rename.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 14: Controller — self route `Link`

`POST /Profile/Me/Emails/Link/{provider}`. Authorizes, then calls `SignInManager.ConfigureExternalAuthenticationProperties(provider, returnUrl)` and returns a `ChallengeResult`.

**Files:**
- Modify: `src/Humans.Web/Controllers/ProfileController.cs`
- Modify: `tests/Humans.Web.Tests/Controllers/ProfileControllerEmailGridTests.cs`

- [ ] **Step 1: Write the failing test.**

```csharp
[Fact]
public async Task Link_AsSelf_ReturnsChallengeResult_WithProvider()
{
    var fixture = new ProfileControllerFixture(actorUserId: SeedUserId);
    var props = new AuthenticationProperties();
    fixture.SignInManager.ConfigureExternalAuthenticationProperties("Google", Arg.Any<string>())
        .Returns(props);

    var result = await fixture.Sut.Link("Google", returnUrl: "/Profile/Me/Emails");

    result.Should().BeOfType<ChallengeResult>()
        .Which.AuthenticationSchemes.Should().Contain("Google");
}
```

- [ ] **Step 2: Run test to verify it fails.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~Link_AsSelf"`

Expected: FAIL — action doesn't exist.

- [ ] **Step 3: Add the action.**

```csharp
[HttpPost("Profile/Me/Emails/Link/{provider}")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> Link(string provider, string? returnUrl = null)
{
    var userId = User.GetUserId();
    var authz = await _authz.AuthorizeAsync(User, userId, UserEmailOperations.Edit);
    if (!authz.Succeeded) return Forbid();

    var redirectUrl = Url.Action(nameof(Emails)) ?? "/Profile/Me/Emails";
    var props = _signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
    return Challenge(props, provider);
}
```

- [ ] **Step 4: Run test to verify it passes.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~Link_AsSelf"`

Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add src tests
git commit -m "$(cat <<'EOF'
feat(profile): self-grid Link controller action

POST /Profile/Me/Emails/Link/{provider} -> ChallengeResult. The OAuth
callback wired in Task 7 calls LinkAsync to attach the new identity to
the current user.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 15: Controller — self route `Unlink`

`POST /Profile/Me/Emails/Unlink/{id}`. Authorizes, calls `_userEmailService.UnlinkAsync`.

**Files:**
- Modify: `src/Humans.Web/Controllers/ProfileController.cs`
- Modify: `tests/Humans.Web.Tests/Controllers/ProfileControllerEmailGridTests.cs`

- [ ] **Step 1: Write the failing test.**

```csharp
[Fact]
public async Task Unlink_AsSelf_CallsUnlinkAsync()
{
    var fixture = new ProfileControllerFixture(actorUserId: SeedUserId);
    var emailId = Guid.NewGuid();

    var result = await fixture.Sut.Unlink(emailId);

    await fixture.UserEmailService.Received(1)
        .UnlinkAsync(SeedUserId, emailId, Arg.Any<CancellationToken>());
    result.Should().BeOfType<RedirectToActionResult>();
}
```

- [ ] **Step 2: Run test to verify it fails.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~Unlink_AsSelf"`

Expected: FAIL — action doesn't exist.

- [ ] **Step 3: Add the action.**

```csharp
[HttpPost("Profile/Me/Emails/Unlink/{id}")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> Unlink(Guid id, CancellationToken ct)
{
    var userId = User.GetUserId();
    var authz = await _authz.AuthorizeAsync(User, userId, UserEmailOperations.Edit);
    if (!authz.Succeeded) return Forbid();

    await _userEmailService.UnlinkAsync(userId, id, ct);
    return RedirectToAction(nameof(Emails));
}
```

- [ ] **Step 4: Run test to verify it passes.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~Unlink_AsSelf"`

Expected: PASS.

- [ ] **Step 5: Commit & push (cumulative push checkpoint — Tasks 6–15).**

```bash
git add src tests
git commit -m "$(cat <<'EOF'
feat(profile): self-grid Unlink controller action

POST /Profile/Me/Emails/Unlink/{id} -> UnlinkAsync. Removes the
AspNetUserLogins row and the email row in one call.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
git push
```

---

## Task 16: Controller — admin routes (six actions, no `AdminLink`)

Six actions parameterized by `{userId}` route param: `AdminSetGoogle`, `AdminSetPrimary`, `AdminAddEmail`, `AdminUnlink`, `AdminDeleteEmail`, `AdminSetVisibility`. Each authorizes via `_authz.AuthorizeAsync(User, userIdFromRoute, UserEmailOperations.Edit)`. **No `AdminLink` action** — Link is self-only.

**Files:**
- Modify: `src/Humans.Web/Controllers/ProfileController.cs`
- Modify: `tests/Humans.Web.Tests/Controllers/ProfileControllerEmailGridTests.cs`

- [ ] **Step 1: Write the failing tests.**

```csharp
[Fact]
public async Task AdminSetGoogle_AsAdmin_CallsServiceWithRouteUserId()
{
    var fixture = new ProfileControllerFixture(actorUserId: AdminUserId, isAdmin: true);
    var targetUserId = Guid.NewGuid();
    var emailId = Guid.NewGuid();

    var result = await fixture.Sut.AdminSetGoogle(targetUserId, emailId);

    await fixture.UserEmailService.Received(1)
        .SetGoogleAsync(targetUserId, emailId, Arg.Any<CancellationToken>());
    result.Should().BeOfType<RedirectToActionResult>();
}

[Fact]
public async Task AdminSetGoogle_AsUnprivilegedOther_ReturnsForbid()
{
    var fixture = new ProfileControllerFixture(actorUserId: Guid.NewGuid(), isAdmin: false);
    var targetUserId = Guid.NewGuid();

    var result = await fixture.Sut.AdminSetGoogle(targetUserId, Guid.NewGuid());

    result.Should().BeOfType<ForbidResult>();
    await fixture.UserEmailService.DidNotReceive()
        .SetGoogleAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
}
```

(Repeat the auth-gate test once for representative coverage; the parallel actions share the gate.)

- [ ] **Step 2: Run tests to verify they fail.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AdminSetGoogle"`

Expected: FAIL — actions don't exist.

- [ ] **Step 3: Add the six admin actions.**

```csharp
[HttpPost("Profile/Admin/{userId}/Emails/SetGoogle")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> AdminSetGoogle(Guid userId, Guid emailId, CancellationToken ct)
{
    var authz = await _authz.AuthorizeAsync(User, userId, UserEmailOperations.Edit);
    if (!authz.Succeeded) return Forbid();

    await _userEmailService.SetGoogleAsync(userId, emailId, ct);
    return RedirectToAction(nameof(AdminEmails), new { userId });
}

[HttpPost("Profile/Admin/{userId}/Emails/SetPrimary")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> AdminSetPrimary(Guid userId, Guid emailId, CancellationToken ct)
{
    var authz = await _authz.AuthorizeAsync(User, userId, UserEmailOperations.Edit);
    if (!authz.Succeeded) return Forbid();

    await _userEmailService.SetPrimaryAsync(userId, emailId, ct);
    return RedirectToAction(nameof(AdminEmails), new { userId });
}

[HttpPost("Profile/Admin/{userId}/Emails/Add")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> AdminAddEmail(Guid userId, string email, CancellationToken ct)
{
    var authz = await _authz.AuthorizeAsync(User, userId, UserEmailOperations.Edit);
    if (!authz.Succeeded) return Forbid();

    await _userEmailService.AddEmailAsync(userId, email, ct);
    return RedirectToAction(nameof(AdminEmails), new { userId });
}

[HttpPost("Profile/Admin/{userId}/Emails/Unlink/{emailId}")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> AdminUnlink(Guid userId, Guid emailId, CancellationToken ct)
{
    var authz = await _authz.AuthorizeAsync(User, userId, UserEmailOperations.Edit);
    if (!authz.Succeeded) return Forbid();

    await _userEmailService.UnlinkAsync(userId, emailId, ct);
    return RedirectToAction(nameof(AdminEmails), new { userId });
}

[HttpPost("Profile/Admin/{userId}/Emails/{emailId}")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> AdminDeleteEmail(Guid userId, Guid emailId, CancellationToken ct)
{
    var authz = await _authz.AuthorizeAsync(User, userId, UserEmailOperations.Edit);
    if (!authz.Succeeded) return Forbid();

    await _userEmailService.DeleteEmailAsync(userId, emailId, ct);
    return RedirectToAction(nameof(AdminEmails), new { userId });
}

[HttpPost("Profile/Admin/{userId}/Emails/SetVisibility")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> AdminSetVisibility(
    Guid userId, Guid emailId, ContactFieldVisibility visibility, CancellationToken ct)
{
    var authz = await _authz.AuthorizeAsync(User, userId, UserEmailOperations.Edit);
    if (!authz.Succeeded) return Forbid();

    await _userEmailService.SetVisibilityAsync(userId, emailId, visibility, ct);
    return RedirectToAction(nameof(AdminEmails), new { userId });
}
```

Also add the `AdminEmails(Guid userId)` GET action that returns the same `Emails.cshtml` view with `IsAdminContext = true` and `RoutePrefix = $"/Profile/Admin/{userId}/Emails"` on the view-model.

- [ ] **Step 4: Run tests.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~Admin"`

Expected: 0 failures.

- [ ] **Step 5: Commit.**

```bash
git add src tests
git commit -m "$(cat <<'EOF'
feat(profile): admin grid controller actions (six, no AdminLink)

/Profile/Admin/{userId}/Emails/... mirrors six of the seven self
actions against a target user. Each authorizes via
UserEmailOperations.Edit (succeeds for self-or-admin). No AdminLink
action — Link is self-only per spec.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 17: GDPR export key `IsOAuth` → `IsGoogle` in `ProfileService`

Change the projection at `ProfileService.cs:~869` to emit `"IsGoogle"` sourced from the row's `IsGoogle` column (not `Provider != null`).

**Files:**
- Modify: `src/Humans.Application/Services/Profile/ProfileService.cs`
- Modify: `tests/Humans.Application.Tests/Services/Profile/ProfileServiceGdprExportTests.cs` (extend if exists)

- [ ] **Step 1: Locate the projection.**

Run: `grep -n "IsOAuth" src/Humans.Application/Services/Profile/ProfileService.cs`

Expected: a single line near 869 in the GDPR export builder.

- [ ] **Step 2: Write the failing test.**

```csharp
[Fact]
public async Task ExportProfileAsync_EmitsIsGoogleKey_FromIsGoogleColumn()
{
    using var fixture = new ProfileServiceFixture();
    var userId = Guid.NewGuid();
    await fixture.SeedUserEmailAsync(userId, "x@google.test", isGoogle: true, provider: null);

    var json = await fixture.Sut.ExportProfileAsync(userId, default);

    json.Should().Contain("\"IsGoogle\":true");
    json.Should().NotContain("\"IsOAuth\"");
}
```

- [ ] **Step 3: Run test to verify it fails.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~ExportProfileAsync_EmitsIsGoogleKey"`

Expected: FAIL — current export still emits `"IsOAuth"`.

- [ ] **Step 4: Update the projection.**

Replace the `IsOAuth = ...` field of the export DTO with `IsGoogle = e.IsGoogle`. Update any DTO type involved if it has a serialized `IsOAuth` property — rename to `IsGoogle`. (Check this is not a serialized class with stored history; the GDPR export is a synthetic per-request projection, so renaming the field name is safe — but verify.)

- [ ] **Step 5: Run test.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~ExportProfileAsync_EmitsIsGoogleKey"`

Expected: PASS.

- [ ] **Step 6: Commit.**

```bash
git add src tests
git commit -m "$(cat <<'EOF'
fix(profile): GDPR export key IsOAuth -> IsGoogle (sourced from column)

Synthetic per-request projection. Sources from the row's IsGoogle
column rather than (Provider != null) so the export reflects the
canonical Workspace identity rather than the OAuth attachment state.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 18: `AccountMergeService.AcceptAsync` audit + rule tests

Walk the method against parent-spec §184–190. Add any missing rule. One test per rule.

**Files:**
- Modify: `src/Humans.Application/Services/Profile/AccountMergeService.cs` (only if a rule is missing)
- Modify: `src/Humans.Application/Interfaces/Profiles/IAccountMergeService.cs` (verify `GetPendingEmailIdsAsync` exists; add if missing)
- Create: `tests/Humans.Application.Tests/Services/Profile/AccountMergeServiceRulesTests.cs`

- [ ] **Step 1: Audit `AcceptAsync`.**

Open `AccountMergeService.AcceptAsync`. Check each rule against parent-spec §184–190:

1. `UserEmails` fold with OR-combine of `IsVerified`/`IsPrimary`/`IsGoogle`; target's `IsPrimary` and `IsGoogle` preserved.
2. `AspNetUserLogins` re-FK; constraint conflict → `Failed` state + admin-required.
3. `EventParticipation` highest-status wins.
4. `CommunicationPreference` most-recent `UpdatedAt` wins.
5. `Tickets` re-FK with no resolution.
6. `RoleAssignment` re-FK with no resolution.
7. `AuditLog` re-FK with no resolution.

Write down for each: PRESENT or MISSING. PRESENT → test only. MISSING → add rule + test.

- [ ] **Step 2: Verify `IAccountMergeService.GetPendingEmailIdsAsync` exists.**

Run: `grep -n "GetPendingEmailIdsAsync" src/Humans.Application/`

Expected: an interface method `Task<IReadOnlySet<Guid>> GetPendingEmailIdsAsync(Guid userId, CancellationToken ct)` (or similar shape). If missing, add to `IAccountMergeService` and implement in `AccountMergeService`. Implementation: return the set of `UserEmailId`s on `AccountMergeRequest` rows in `Submitted` state where the target user is `userId`.

- [ ] **Step 3: Write rule tests.**

```csharp
[Fact]
public async Task AcceptAsync_UserEmailsFold_OrCombinesFlags_AndPreservesTargetPrimaryAndGoogle()
{
    // Seed source User S with row (IsVerified=true, IsPrimary=false, IsGoogle=true).
    // Seed target User T with row (same Email, IsVerified=false, IsPrimary=true, IsGoogle=false).
    // Submit + Accept the merge.
    // Assert single row on T: IsVerified=true (OR), IsPrimary=true (target preserved),
    //                       IsGoogle=false (target preserved).
}

[Fact]
public async Task AcceptAsync_AspNetUserLoginsConflict_TransitionsToFailed_AdminRequired()
{
    // Source S has AspNetUserLogins (Google, sub-X).
    // Target T already has AspNetUserLogins (Google, sub-X) — same key.
    // Accept -> Failed state, admin-required flag set.
}

[Fact]
public async Task AcceptAsync_EventParticipation_HighestStatusWins() { /* ... */ }

[Fact]
public async Task AcceptAsync_CommunicationPreference_MostRecentUpdatedAtWins() { /* ... */ }

[Fact]
public async Task AcceptAsync_TicketsRoleAssignmentAuditLog_AreReFKed() { /* ... */ }
```

- [ ] **Step 4: Run tests.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AccountMergeServiceRulesTests"`

Expected: tests pass for present rules; fail for any missing rule. Add missing rules (small in-place patches to `AcceptAsync`) until all pass.

- [ ] **Step 5: Commit.**

```bash
git add src tests
git commit -m "$(cat <<'EOF'
test(profile): AccountMergeService.AcceptAsync §184-190 rule coverage

Adds one test per parent-spec rule. Any missing rule was added in this
commit (see diff in AccountMergeService.cs). Confirms OR-combine of
flags with target's IsPrimary/IsGoogle preserved.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 19: View-model — `EmailsViewModel` properties for grid context

Add `MergePendingEmailIds`, `IsAdminContext`, `RoutePrefix`, `TargetUserId`. Populate from controller. Self route sets `IsAdminContext = false`, `RoutePrefix = "/Profile/Me/Emails"`, `TargetUserId = currentUserId`. Admin route sets `IsAdminContext = true`, `RoutePrefix = $"/Profile/Admin/{userId}/Emails"`, `TargetUserId = userId`.

**Files:**
- Modify: `src/Humans.Web/Models/EmailsViewModel.cs`
- Modify: `src/Humans.Web/Controllers/ProfileController.cs` (the existing `Emails` GET + new `AdminEmails` GET)

- [ ] **Step 1: Add the properties.**

```csharp
public sealed class EmailsViewModel
{
    // existing fields...
    public Guid TargetUserId { get; init; }
    public string RoutePrefix { get; init; } = "/Profile/Me/Emails";
    public bool IsAdminContext { get; init; }
    public IReadOnlySet<Guid> MergePendingEmailIds { get; init; } = new HashSet<Guid>();
}
```

(Adapt to the existing model's record/class shape.)

- [ ] **Step 2: Populate in the self `Emails` GET.**

```csharp
var userId = User.GetUserId();
var rows = await _userEmailService.GetByUserIdAsync(userId, ct);
var pending = await _accountMergeService.GetPendingEmailIdsAsync(userId, ct);

return View(new EmailsViewModel
{
    Rows = rows,
    TargetUserId = userId,
    RoutePrefix = "/Profile/Me/Emails",
    IsAdminContext = false,
    MergePendingEmailIds = pending,
});
```

- [ ] **Step 3: Add the admin `AdminEmails` GET (if not already added in Task 16).**

```csharp
[HttpGet("Profile/Admin/{userId}/Emails")]
public async Task<IActionResult> AdminEmails(Guid userId, CancellationToken ct)
{
    var authz = await _authz.AuthorizeAsync(User, userId, UserEmailOperations.Edit);
    if (!authz.Succeeded) return Forbid();

    var rows = await _userEmailService.GetByUserIdAsync(userId, ct);
    var pending = await _accountMergeService.GetPendingEmailIdsAsync(userId, ct);

    return View("Emails", new EmailsViewModel
    {
        Rows = rows,
        TargetUserId = userId,
        RoutePrefix = $"/Profile/Admin/{userId}/Emails",
        IsAdminContext = true,
        MergePendingEmailIds = pending,
    });
}
```

- [ ] **Step 4: Build.**

Run: `dotnet build Humans.slnx -v quiet`

Expected: 0 errors.

- [ ] **Step 5: Commit.**

```bash
git add src
git commit -m "$(cat <<'EOF'
feat(profile): EmailsViewModel routing/context fields

Adds TargetUserId/RoutePrefix/IsAdminContext/MergePendingEmailIds to
support a single Emails.cshtml rendering both self and admin grids.
Populated from the controller; self routes use defaults.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 20: View — rewrite `Views/Profile/Emails.cshtml`

Per spec §View structure. Six columns (Email + Provider badge / Status with `MergePending` pill / Primary radio / Google radio enabled on any verified row / Visibility / single contextual action button). Below grid: Add email + Link Google account button (hidden when `IsAdminContext`). Admin banner when `IsAdminContext`.

**Files:**
- Modify: `src/Humans.Web/Views/Profile/Emails.cshtml`

- [ ] **Step 1: Lay out the table.**

Replace the current grid body with:

```cshtml
@model Humans.Web.Models.EmailsViewModel

@if (Model.IsAdminContext)
{
    <div class="alert alert-warning">
        @SharedLocalizer["EmailGrid_AdminContextBanner", Model.TargetDisplayName]
    </div>
}

<table class="table">
    <thead>
        <tr>
            <th>@SharedLocalizer["EmailGrid_Email"]</th>
            <th>@SharedLocalizer["EmailGrid_Status"]</th>
            <th>@SharedLocalizer["EmailGrid_Primary"]</th>
            <th title="@SharedLocalizer["EmailGrid_GoogleColumnTooltip"]">@SharedLocalizer["EmailGrid_Google"]</th>
            <th>@SharedLocalizer["EmailGrid_Visibility"]</th>
            <th>@SharedLocalizer["EmailGrid_Actions"]</th>
        </tr>
    </thead>
    <tbody>
    @foreach (var row in Model.Rows)
    {
        <tr>
            <td>
                @row.Email
                @if (!string.IsNullOrEmpty(row.Provider))
                {
                    <span class="badge bg-info">@row.Provider</span>
                }
            </td>
            <td>
                @if (Model.MergePendingEmailIds.Contains(row.Id))
                {
                    <span class="badge bg-warning">@SharedLocalizer["EmailGrid_StatusMergePending"]</span>
                }
                else if (row.IsVerified)
                {
                    <span class="badge bg-success">@SharedLocalizer["EmailGrid_StatusVerified"]</span>
                }
                else
                {
                    <span class="badge bg-secondary">@SharedLocalizer["EmailGrid_StatusPending"]</span>
                }
            </td>
            <td>
                <form asp-action="@(Model.IsAdminContext ? "AdminSetPrimary" : "SetPrimary")"
                      asp-route-userId="@(Model.IsAdminContext ? Model.TargetUserId : (Guid?)null)"
                      method="post">
                    <input type="hidden" name="emailId" value="@row.Id" />
                    <input type="radio" name="primary" @(row.IsPrimary ? "checked" : "")
                           onchange="this.form.submit()" />
                </form>
            </td>
            <td>
                <form asp-action="@(Model.IsAdminContext ? "AdminSetGoogle" : "SetGoogle")"
                      asp-route-userId="@(Model.IsAdminContext ? Model.TargetUserId : (Guid?)null)"
                      method="post">
                    <input type="hidden" name="emailId" value="@row.Id" />
                    <input type="radio" name="google" @(row.IsGoogle ? "checked" : "")
                           @(row.IsVerified ? "" : "disabled")
                           onchange="this.form.submit()" />
                </form>
            </td>
            <td>
                @* existing visibility dropdown — repoint asp-action to the prefixed action *@
            </td>
            <td>
                @if (!string.IsNullOrEmpty(row.Provider))
                {
                    <form asp-action="@(Model.IsAdminContext ? "AdminUnlink" : "Unlink")"
                          asp-route-userId="@(Model.IsAdminContext ? Model.TargetUserId : (Guid?)null)"
                          asp-route-id="@row.Id"
                          asp-route-emailId="@row.Id"
                          method="post"
                          onsubmit="return confirm(@SharedLocalizer["EmailGrid_UnlinkConfirm"]);">
                        <button type="submit" class="btn btn-sm btn-outline-danger">
                            @SharedLocalizer["EmailGrid_UnlinkGoogleAccount"]
                        </button>
                    </form>
                }
                else
                {
                    <form asp-action="@(Model.IsAdminContext ? "AdminDeleteEmail" : "DeleteEmail")"
                          asp-route-userId="@(Model.IsAdminContext ? Model.TargetUserId : (Guid?)null)"
                          asp-route-id="@row.Id"
                          asp-route-emailId="@row.Id"
                          method="post"
                          onsubmit="return confirm(@SharedLocalizer["EmailGrid_DeleteConfirm"]);">
                        <button type="submit" class="btn btn-sm btn-outline-danger">
                            @SharedLocalizer["EmailGrid_Delete"]
                        </button>
                    </form>
                }
            </td>
        </tr>
    }
    </tbody>
</table>

@* Add email + Link Google account — hidden in admin context *@
@if (!Model.IsAdminContext)
{
    @* existing magic-link Add Email form *@
    <form asp-action="Link" asp-route-provider="Google" method="post">
        <button type="submit" class="btn btn-outline-primary">
            @SharedLocalizer["EmailGrid_LinkGoogleAccount"]
        </button>
    </form>
    <small>@SharedLocalizer["EmailGrid_LinkGoogleHelp"]</small>
}
```

(The exact tag-helper invocation depends on the project's existing tag-helper conventions. If `asp-action` requires the controller to be set, add `asp-controller="Profile"`. Match the existing Emails.cshtml top of file for consistency.)

- [ ] **Step 2: Build.**

Run: `dotnet build Humans.slnx -v quiet`

Expected: 0 errors.

- [ ] **Step 3: Smoke-render the page locally.**

Run: `dotnet run --project src/Humans.Web` and visit `https://nuc.home:NNNN/Profile/Me/Emails`. Confirm the grid renders for the dev login user, the Link Google button appears, and the per-row buttons toggle correctly between "Unlink Google account" and "Delete".

- [ ] **Step 4: Commit & push (cumulative push checkpoint — Tasks 16–20).**

```bash
git add src
git commit -m "$(cat <<'EOF'
feat(profile): rewrite Emails.cshtml as parameterized self/admin grid

Single cshtml rendered for both self and admin contexts. Per-row
contextual button (Unlink for Provider-attached rows, Delete for plain).
Link Google account hidden when IsAdminContext. Status pill includes
new MergePending mode.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
git push
```

---

## Task 21: View — `Views/Profile/AdminDetail.cshtml` updates

Relabel the `IsNotificationTarget` bell badge to "Primary" (icon stays). Add a "Manage emails" contextual link to `/Profile/Admin/{userId}/Emails`. (Confirms PR 3's IsOAuth pill removal already in place.)

**Files:**
- Modify: `src/Humans.Web/Views/Profile/AdminDetail.cshtml`

- [ ] **Step 1: Locate the badge.**

Run: `grep -n "IsNotificationTarget\|notification target" src/Humans.Web/Views/Profile/AdminDetail.cshtml`

Expected: a couple of lines for the bell badge.

- [ ] **Step 2: Update the label.**

Replace any localizer key reference like `SharedLocalizer["EmailGrid_NotificationTarget"]` with `SharedLocalizer["EmailGrid_Primary"]`. The bell icon stays.

- [ ] **Step 3: Add the "Manage emails" link.**

Place near the email list (locate the existing email list block):

```cshtml
<a asp-action="AdminEmails" asp-route-userId="@Model.UserId"
   class="btn btn-sm btn-outline-secondary">
    @SharedLocalizer["AdminDetail_ManageEmails"]
</a>
```

- [ ] **Step 4: Confirm the IsOAuth pill is gone.**

Run: `grep -n "IsOAuth\|OAuth pill" src/Humans.Web/Views/Profile/AdminDetail.cshtml`

Expected: no matches (PR 3 cleaned this; the line is verification only).

- [ ] **Step 5: Build.**

Run: `dotnet build Humans.slnx -v quiet`

Expected: 0 errors.

- [ ] **Step 6: Commit.**

```bash
git add src
git commit -m "$(cat <<'EOF'
feat(profile): AdminDetail relabel + Manage emails link

- IsNotificationTarget bell badge label -> "Primary" (icon unchanged).
- Adds "Manage emails" contextual link to /Profile/Admin/{userId}/Emails
  satisfying the no-orphan-pages rule.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 22: Localization (resx, all locales)

Add `EmailGrid_StatusMergePending`, `EmailGrid_LinkGoogleAccount`, `EmailGrid_UnlinkGoogleAccount`, `EmailGrid_GoogleColumnTooltip` (plus any helper keys referenced in Task 20 — `EmailGrid_AdminContextBanner`, `EmailGrid_UnlinkConfirm`, `EmailGrid_DeleteConfirm`, `EmailGrid_LinkGoogleHelp`, `EmailGrid_Google`, `AdminDetail_ManageEmails`). Rename `EmailGrid_NotificationTarget` → `EmailGrid_Primary` (delete the old key, no shadow keys).

**Files:**
- Modify: `src/Humans.Web/Resources/SharedResource.en.resx`
- Modify: `src/Humans.Web/Resources/SharedResource.es.resx`
- Modify: `src/Humans.Web/Resources/SharedResource.de.resx`
- Modify: `src/Humans.Web/Resources/SharedResource.fr.resx`
- Modify: `src/Humans.Web/Resources/SharedResource.it.resx`

(Adjust filenames to match the project's resx layout — verify with `ls src/Humans.Web/Resources/`.)

- [ ] **Step 1: For each locale, add the new keys and rename the existing one.**

For en (use translations literally; per the org's branding rule "humans" stays English in all locales — the email-grid strings have no "humans" reference but apply normal translations otherwise):

```xml
<data name="EmailGrid_StatusMergePending" xml:space="preserve">
  <value>Merge pending</value>
</data>
<data name="EmailGrid_LinkGoogleAccount" xml:space="preserve">
  <value>Link Google account</value>
</data>
<data name="EmailGrid_UnlinkGoogleAccount" xml:space="preserve">
  <value>Unlink Google account</value>
</data>
<data name="EmailGrid_GoogleColumnTooltip" xml:space="preserve">
  <value>Email used for Google Workspace sync.</value>
</data>
```

Plus any helper keys you used in the Task 20 view that don't yet exist (search the resx for each before adding).

For es/de/fr/it, follow the project's existing translation style. (If the user wants community-translated strings, leave the English fallback in those resx files and tag with a `<comment>TODO: translate</comment>` per existing convention.)

- [ ] **Step 2: Rename `EmailGrid_NotificationTarget` → `EmailGrid_Primary` in all five resx files.**

Delete the old `<data name="EmailGrid_NotificationTarget">` block. Add `<data name="EmailGrid_Primary"><value>Primary</value></data>` (translate per locale).

- [ ] **Step 3: Build.**

Run: `dotnet build Humans.slnx -v quiet`

Expected: 0 errors.

- [ ] **Step 4: Confirm no shadow keys.**

Run: `grep -rn "EmailGrid_NotificationTarget" src/Humans.Web/`

Expected: zero matches.

- [ ] **Step 5: Commit.**

```bash
git add src
git commit -m "$(cat <<'EOF'
feat(localization): email grid PR 4 keys (en/es/de/fr/it)

Adds EmailGrid_StatusMergePending, EmailGrid_LinkGoogleAccount,
EmailGrid_UnlinkGoogleAccount, EmailGrid_GoogleColumnTooltip across
all five locales. Renames EmailGrid_NotificationTarget ->
EmailGrid_Primary; old key deleted (no shadow keys).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 23: Cross-User merge integration test + admin auth gate test

New file `tests/Humans.Web.Tests/Profile/EmailGridFlowTests.cs`. Three tests per the spec.

**Files:**
- Create: `tests/Humans.Web.Tests/Profile/EmailGridFlowTests.cs`

- [ ] **Step 1: Write the tests.**

```csharp
public class EmailGridFlowTests : IClassFixture<HumansWebTestFixture>
{
    private readonly HumansWebTestFixture _fx;

    public EmailGridFlowTests(HumansWebTestFixture fx) => _fx = fx;

    [Fact]
    public async Task SelfAddEmail_AlreadyVerifiedOnAnotherUser_CreatesAccountMergeRequest_AndShowsMergePendingPill()
    {
        // Seed User A and User B; B has verified UserEmail "shared@x.test".
        // As A, POST /Profile/Me/Emails/AddEmail with email=shared@x.test (magic-link path).
        // Assert AccountMergeRequest in Submitted state, source=A, target=B.
        // GET /Profile/Me/Emails -> response body contains the MergePending badge for the row.
    }

    [Fact]
    public async Task UnprivilegedUser_AdminPostToOtherUserEmails_Forbidden()
    {
        // Seed User A (target) and User C (unprivileged).
        // As C, POST /Profile/Admin/{A.Id}/Emails/SetGoogle with emailId.
        // Assert HTTP 403; service was never called.
        // Repeat: POST /Profile/Admin/{A.Id}/Emails/Unlink/{emailId}.
        // Assert HTTP 403; service was never called.
    }

    [Fact]
    public async Task AdminAddEmail_AlreadyVerifiedOnAnotherUser_StillCreatesMergeRequest_NoForceAddBypass()
    {
        // Seed User A (target), User B (other, verified email "shared@x.test"),
        // and Admin user (in Admin role).
        // As Admin, POST /Profile/Admin/{A.Id}/Emails/Add with email=shared@x.test.
        // Assert AccountMergeRequest created (source=A, target=B), Submitted state.
        // Assert no row is silently merged.
    }

    [Fact]
    public async Task AdminLinkRoute_DoesNotExist()
    {
        // Direct POST /Profile/Admin/{A.Id}/Emails/Link/Google as admin.
        // Assert HTTP 404 (no route) or 405 (method not allowed); confirm no
        // admin-link backdoor.
    }
}
```

- [ ] **Step 2: Run tests.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~EmailGridFlowTests"`

Expected: 4 passed.

- [ ] **Step 3: Commit.**

```bash
git add tests
git commit -m "$(cat <<'EOF'
test(profile): EmailGridFlowTests — cross-user merge + admin auth gates

Covers the four spec-required integration scenarios: self-add cross-user
collision creates merge request and shows MergePending pill; unprivileged
user gets 403 on admin SetGoogle / Unlink; admin-driven cross-user merge
still creates a merge request (no force-add bypass); /Profile/Admin/
{userId}/Emails/Link/{provider} does not exist.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 24: Final — full build, full tests, manual smoke, push, PR

- [ ] **Step 1: Full build.**

Run: `dotnet build Humans.slnx -v quiet`

Expected: 0 warnings, 0 errors.

- [ ] **Step 2: Full test suite.**

Run: `dotnet test Humans.slnx -v quiet`

Expected: all suites green.

- [ ] **Step 3: Architecture-test sweep.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UserArchitectureTests|FullyQualifiedName~UserEmailLegacyFieldRestrictionsTests|FullyQualifiedName~InterfaceMethodBudgetTests|FullyQualifiedName~IdentityColumnWriteRestrictionsTests"`

Expected: 0 failures.

- [ ] **Step 4: EF migration sanity probe.**

Run: `dotnet ef migrations add ProbeNoSchemaChanges --project src/Humans.Infrastructure --startup-project src/Humans.Web`

Expected: empty `Up`/`Down` bodies. STOP if non-empty — this PR has no schema changes; if EF wants to write something, the column-pinning in Task 1 is wrong. Do not hand-edit the migration. Then:

Run: `dotnet ef migrations remove --project src/Humans.Infrastructure --startup-project src/Humans.Web`

- [ ] **Step 5: Manual smoke (per spec §Manual smoke, on local or PR preview).**

Walk through:
- Add email via magic link → verify → flag Primary/Google → delete (plain row → Delete button).
- Link Google account → callback returns to grid → "Google" badge on row.
- Unlink a Provider-attached row → row gone, AspNetUserLogins gone, can re-Link the same email afterward.
- Per-row button is Unlink on Provider-attached rows and Delete on plain rows — never both.
- Cross-User collision → MergePending pill shows.
- Admin AcceptAsync of merge → emails fold correctly.
- Set IsGoogle on a `proton.me` magic-link row → Workspace sync uses that address.
- Admin: navigate to a target user's profile admin detail → Manage emails → run all six admin actions → confirm changes apply, audit-log records actor=admin / subject=target. Confirm Link Google account button NOT rendered on the admin grid.
- Non-admin user: direct GET/POST to `/Profile/Admin/{other-user-id}/Emails/...` → expect 403.
- Admin: direct POST to `/Profile/Admin/{other-user-id}/Emails/Link/Google` → expect 404 or 405; no admin-link backdoor.

Document any deviations from expected behavior in PR comments before merge.

- [ ] **Step 6: Push final branch.**

```bash
git push
```

- [ ] **Step 7: Open PR.**

```bash
gh pr create --repo peterdrier/Humans --base main \
  --title "Email-identity decoupling PR 4 — grid + Link surface + admin parity" \
  --body "$(cat <<'EOF'
PR 4 of the email-identity-decoupling sequence. Spec at
docs/superpowers/specs/2026-04-30-email-oauth-pr4-grid-and-link.md.

## Summary

- **Profile email grid rewrite** — single Emails.cshtml parameterized
  for self and admin contexts. Per-row contextual button: "Unlink Google
  account" on Provider-attached rows, "Delete" on plain rows. Bottom-of-
  grid "Link Google account" (self only).
- **Service surface** — adds SetGoogleAsync, LinkAsync (consolidates the
  two deleted AddOAuthEmailAsync + SetProviderAsync), UnlinkAsync.
  DeleteEmailAsync gains a Provider-attached precondition.
- **Admin route family** — /Profile/Admin/{userId}/Emails/... with six
  actions (no AdminLink — Link is self-only because admins cannot drive
  the target's OAuth flow). Authorization via UserEmailAuthorizationHandler
  (resource = target user id, succeeds for self or Admin role).
- **Renames** — UserEmail.IsNotificationTarget -> IsPrimary (column pinned),
  SetNotificationTargetAsync -> SetPrimaryAsync, RewriteOAuthEmailAsync ->
  RewriteLinkedEmailAsync. Architecture test forbids "OAuth" token in
  IUserEmailService / UserEmailRepository names going forward.
- **GDPR export** — IsOAuth -> IsGoogle key sourced from the column.
- **AccountMergeService.AcceptAsync** — verified against parent-spec
  §184–190 rules; rule tests added.

No schema changes. dotnet ef migrations add probe produces an empty
migration.

## Test plan

- [ ] CI green
- [ ] Codex review clean (do not ping)
- [ ] On QA preview: full manual smoke per spec §Manual smoke
- [ ] On QA: admin walks all six admin actions on a target user; audit
      entries record actor=admin / subject=target
- [ ] On QA: cross-user collision via magic link creates AccountMergeRequest
      and the MergePending pill shows on the grid
EOF
)"
```

- [ ] **Step 8: Wait for Codex review (do not ping). Address each finding via thread-reply on the inline comment** (`feedback_codex_thread_replies`). Done = Codex-clean + CI green + every finding fixed or explained (`feedback_done_means_codex_clean`, `feedback_done_means_done`). Check both `peterdrier/Humans` and `nobodies-collective/Humans` for review comments (`feedback_pr_review_both_repos`).

---

## Self-review checklist

Run through this after writing/before handoff. Fix in-line; don't re-review.

1. **Spec coverage.** Every PR-4 bullet in the spec maps to a task above. Spec §Goals 1–7 → Tasks 4–5 (SetGoogle), 14 (Link), 9 + 11 (Unlink + DeleteEmail precondition), 19 + 20 + 23 (MergePending pill), 20 (Provider badge data-driven), 10 (no OAuth in method names), 12 + 16 (admin parity). Spec §Service & repo changes → Tasks 4, 5, 6, 9, 11, 1, 2, 3, 18. Spec §Controller actions → Tasks 7, 13–16. Spec §View structure → 19, 20, 21. Spec §Localization → 22. Spec §Tests → all relevant tasks have a TDD step + Tasks 18, 23.
2. **No placeholder language.** No "TBD", "see below", "etc." Every step has either code, a specific file path, or a concrete shell command.
3. **Type consistency.** `SetGoogleAsync(Guid userId, Guid userEmailId, CancellationToken)` consistently three args. `LinkAsync(Guid userId, string provider, string providerKey, string email, CancellationToken)` consistently five args. `UnlinkAsync(Guid userId, Guid userEmailId, CancellationToken)` three args. The two new audit actions are `UserEmailGoogleSet`, `UserEmailLinked`, `UserEmailUnlinked`. Authorization handler resource is `Guid targetUserId`. Confirmed across Tasks 5, 6, 9, 12, 13, 14, 15, 16.
4. **Architecture test placed early.** Task 10 — after Task 9 (Unlink) and before Task 11 (DeleteEmailAsync precondition). Subsequent renames must clear it.
5. **Push cadence.** Explicit `git push` at Tasks 5, 15, 20, and 24. Cumulative deltas, no big-bang push.
6. **No splits to followups.** Everything in spec PR 4 scope is here. The spec's already-out-of-scope items (PR 7 column drops, AccountMergeRequest review-queue UI, removing `IUserService.SetGoogleEmailAsync`) are NOT in this plan, correctly.
7. **Hard-rule audit.** No DB drops. No DB unique indexes. No hand-edited migration (Task 24 Step 4 verifies the empty-migration invariant). No startup guards. No concurrency tokens. No invented fields. ✓.

---

## Open questions / unverified dependencies (raise at plan-review or impl)

1. **`_fullProfileInvalidator`.** Plan assumes a `FullProfile` cache invalidator is already injected into `UserEmailService` (PR 3 references this pattern). Verify at impl time; if absent, follow the existing `UserEmailService` cache-invalidation pattern (PR 3 added one of these — adapt).
2. **`IAccountMergeService.GetPendingEmailIdsAsync`.** Task 18 Step 2 verifies this exists; if not, the method is added in Task 18. Plan does not pre-budget for the interface ratchet — `IAccountMergeService` is not currently in the budget list per PR 3's note about `IUserEmailService` not being on the budget either, so an additive method is fine. If the budget test fails, STOP and surface a list of removal candidates rather than expanding the budget.
3. **`_currentUserAccessor`.** Plan references this for actor resolution in audit-log calls. Verify the project's actual accessor name at impl time; commit `7bde7a96` is the canonical reference for the actor/subject split pattern.
4. **`UserEmailServiceFixture` / `ProfileControllerFixture`.** Plan references both as if they exist; verify at impl. If a service-test fixture pattern doesn't yet exist for `UserEmailService` post-PR3, copy the closest existing service-test fixture in the repo and adapt.
5. **Resx file naming.** Plan assumes `SharedResource.{locale}.resx` under `src/Humans.Web/Resources/`. Verify with `ls src/Humans.Web/Resources/` at Task 22 Step 1; the actual file naming may differ.
