# AccountMergeService Fold-into-Target Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Change `AccountMergeService.AcceptAsync` from anonymize-source to fold-into-target. Source User row becomes a tombstone (`MergedToUserId`, `MergedAt`); cross-section data re-FKs to target with per-section conflict rules; immutable rows (AuditLog, Consent, BudgetAuditLog) stay at source and are surfaced for target via a `MergedToUserId` chain-follow on read.

**Architecture:** Each cross-section service gains a `Reassign…ToUserAsync(sourceUserId, targetUserId, now, ct)` that bulk-moves rows + applies the section's conflict rule. The merge orchestrator calls section services in dependency order inside the existing `TransactionScope`. Three immutable-row sections (AuditLog, Consent, BudgetAuditLog) gain chain-follow on per-user reads via a single new lookup primitive, `IUserService.GetMergedSourceIdsAsync`.

**Tech Stack:** ASP.NET Core 10, EF Core (Npgsql), NodaTime, xUnit / AwesomeAssertions, TransactionScope w/ AsyncFlowOption.

**Spec:** [`docs/superpowers/specs/2026-04-30-account-merge-fold-redesign.md`](../specs/2026-04-30-account-merge-fold-redesign.md)
**Parent sequence:** [`docs/superpowers/specs/2026-04-27-email-and-oauth-decoupling-design.md`](../specs/2026-04-27-email-and-oauth-decoupling-design.md) (PR 4 task 18)

---

## File Map

### Created

- `src/Humans.Infrastructure/Migrations/{stamp}_AddUserMergedToUserId.cs` (auto-generated)
- `src/Humans.Infrastructure/Migrations/HumansDbContextModelSnapshot.cs` (auto-updated)
- Tests: `tests/Humans.IntegrationTests/AccountMerge/AcceptAsyncFoldTests.cs` (one class, all 22 re-FK tests)
- Tests: `tests/Humans.IntegrationTests/AccountMerge/ChainFollowReadTests.cs` (3 chain-follow tests)
- Tests: `tests/Humans.IntegrationTests/AccountMerge/AcceptAsyncFullFixtureTest.cs` (one full-section fixture)

### Modified — Domain entity

- `src/Humans.Domain/Entities/User.cs` — adds `MergedToUserId`, `MergedAt`

### Modified — Section service interfaces (each gains a Reassign method; some incur budget ratchet)

| Section interface | Method added | Budget? | Removal needed |
|---|---|---|---|
| `IUserEmailService` | `ReassignToUserAsync` | no | none |
| `IUserService` | `ReassignLoginsToUserAsync`, `ReassignEventParticipationToUserAsync`, `AnonymizeForMergeAsync`, `GetMergedSourceIdsAsync` | **31** | **−4** |
| `IProfileService` | `ReassignSubAggregatesToUserAsync` | **41** | **−1** |
| `IContactFieldService` | `ReassignToUserAsync` | no | none |
| `ICommunicationPreferenceService` | `ReassignToUserAsync` | no | none |
| `ITicketSyncService` | `ReassignToUserAsync` | no | none |
| `IRoleAssignmentService` | `ReassignToUserAsync` | no | none |
| `ITeamService` | `ReassignToUserAsync` | **71** | **−1** |
| `IShiftSignupService` | `ReassignToUserAsync` | no | none |
| `IShiftManagementService` | `ReassignProfilesAndTagPrefsToUserAsync` | **50** | **−1** |
| `IGeneralAvailabilityService` | `ReassignToUserAsync` | no | none |
| `INotificationService` | `ReassignRecipientsToUserAsync` | no | none |
| `ICampaignService` | `ReassignGrantsToUserAsync` | no | none |
| `ICampService` | `ReassignAssignmentsToUserAsync` | **55** | **−1** |
| `IApplicationDecisionService` | `ReassignApplicationsToUserAsync` | no | none |
| `IFeedbackService` | `ReassignToUserAsync` | no | none |

For each budgeted interface, run `/audit-surface <Interface>` at execution time. If no dead method exists, **STOP and ask Peter** (per `architecture_interface_budget_ratchet_down_only`). Decrement the budget in `tests/Humans.Application.Tests/Architecture/InterfaceMethodBudgetTests.cs` to match the new count.

### Modified — Repos (each section's repo gains a `Reassign…` bulk-update returning a count)

Each section's repo (under `src/Humans.Infrastructure/Repositories/...`) gains a method matching the new service surface. Pattern: `Task<int> ReassignToUserAsync(Guid sourceUserId, Guid targetUserId, Instant updatedAt, CancellationToken ct)`. Implementation runs an `ExecuteUpdateAsync` for the move, then a service-layer post-step (or repo helper) for the conflict rule. **No LINQ chains at the service layer over `db.Xxx`** — services call repos that return materialized results.

### Modified — AccountMergeService

- `src/Humans.Application/Services/Profile/AccountMergeService.cs` — rewrite `AcceptAsync` body. Drop direct repo calls (`_userRepository.RemoveExternalLoginsAsync`, `_userRepository.AnonymizeForMergeAsync`, `_userEmailRepository.RemoveAllForUserAndSaveAsync`, `_profileRepository.AnonymizeForMergeByUserIdAsync`). Drop the team add/remove dance in favour of `_teamService.ReassignToUserAsync`. Keep the `TransactionScope`, the audit log entry, the post-commit cache invalidations.

### Modified — Chain-follow read paths (immutable sections)

- `IAuditLogService` impl: every `GetForUser*` / per-user filter unions `IUserService.GetMergedSourceIdsAsync(targetId)` into the `userId` list before querying the repo. Repo methods accept `IReadOnlyCollection<Guid>` (already do, where applicable) or gain an `IReadOnlyCollection<Guid> userIds` overload.
- `IConsentService` impl: same pattern for per-user reads + GDPR export contributor.
- `IBudgetService` impl (BudgetAuditLog read paths only): same pattern.

### Modified — Section invariant docs

- `docs/sections/Auth.md`, `docs/sections/Users.md`, `docs/sections/Profiles.md`, `docs/sections/Teams.md`, `docs/sections/Shifts.md`, `docs/sections/Camps.md`, `docs/sections/Tickets.md`, `docs/sections/Governance.md`, `docs/sections/Feedback.md`, `docs/sections/Calendar.md`, `docs/sections/Notifications.md`, `docs/sections/Audit Log.md`, `docs/sections/Legal & Consent.md`, `docs/sections/Budget.md`, `docs/sections/Campaigns.md` — note the new `Reassign…` surface or chain-follow rule under Triggers / Cross-Section Dependencies as applicable.
- `docs/architecture/data-model.md` — append `User.MergedToUserId`/`MergedAt` to the User entity table; cross-cutting: chain-follow rule for append-only entities.

### Deleted (code-only — exempt from no-drops rule)

- `IUserRepository.RemoveExternalLoginsAsync` if no other caller (verify with `reforge`)
- `IUserRepository.AnonymizeForMergeAsync` superseded by `IUserService.AnonymizeForMergeAsync` *if* the repo method has no other caller; otherwise keep but mark the service as the only entry point and cut the AccountMergeService → repo direct call.
- `IUserEmailRepository.RemoveAllForUserAndSaveAsync` — verify with reforge before deleting.
- `IProfileRepository.AnonymizeForMergeByUserIdAsync` — verify with reforge before deleting.

---

## Risk

- **Interface budget ratchet on 5 interfaces.** `IUserService` (-4) is the highest pressure. `audit-surface` must surface candidates; if not, STOP. Removals must be code-only (not split-into-sub-interface).
- **Discovering an entity FK that isn't in the spec.** The spec was written from `docs/sections/*` reading. If a section actually owns a table not listed, the merge will leave orphan rows. Mitigation: at execution time, for each section, search for `UserId` columns on entities owned by that section before writing the Reassign method. Use `reforge` (find references / find members) for `User`/`Guid UserId` properties.
- **Conflict-rule edge cases.** "Same address" / "same provider+key" / "same campaign" — when both rows exist, drop source's. Each repo method must do this in the right order: insert/update target's, then delete source's, then `SaveChanges`. Wrong order trips unique constraints.
- **Test fixture seeding.** Integration tests need a User+Profile pair on both sides plus per-section seed rows. Build a single helper `SeedSourceAndTargetWithSectionData` returning the IDs.
- **Cache invalidations.** AcceptAsync today invalidates FullProfile + ActiveTeams. The fold orchestration may need extra invalidations (e.g. RoleAssignment cache). Audit the existing post-commit invalidations for completeness against the new surface.

---

## Phase plan (commit phase-tagged into one branch / one PR)

1. **Phase 0 — schema & lookup primitive** (1 commit)
2. **Phase 1 — Profile-section internal services** (1 commit per service)
3. **Phase 2 — Cross-section services, non-budgeted** (1 commit per service; can parallelize via `superpowers:dispatching-parallel-agents`, max 3 in parallel)
4. **Phase 3 — Cross-section services, budgeted** (1 commit per service; sequential; each preceded by `/audit-surface`)
5. **Phase 4 — Chain-follow read paths** (1 commit per immutable section)
6. **Phase 5 — Orchestrator rewrite** (1 commit)
7. **Phase 6 — Tests** (1 commit per test class)
8. **Phase 7 — Doc + dead-code cleanup** (1 commit)

Push every 3–5 tasks (`feedback_push_often_during_long_runs`). `dotnet format Humans.slnx` before each commit.

---

## Phase 0 — Schema & Lookup Primitive

### Task 0.1: Add `MergedToUserId` / `MergedAt` to `User` entity + EF model

**Files:**
- Modify: `src/Humans.Domain/Entities/User.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/UserConfiguration.cs` (or wherever User is configured)

- [ ] **Step 1: Add properties on `User`**

```csharp
// In User.cs — append to the property list (location: after the Identity-managed
// fields, before navigation properties).
/// <summary>
/// When set, marks this user as a tombstone that has been merged into the
/// referenced target user. Reads of "data for the target user" union the
/// IDs of every source whose MergedToUserId points at the target (via
/// IUserService.GetMergedSourceIdsAsync) for append-only row history
/// (AuditLog, ConsentRecord, BudgetAuditLog). Once set, source cannot
/// sign in (LockoutEnd is bumped far-future by AnonymizeForMergeAsync).
/// </summary>
public Guid? MergedToUserId { get; set; }

/// <summary>
/// Instant the merge tombstone was applied. Null while live.
/// </summary>
public Instant? MergedAt { get; set; }
```

- [ ] **Step 2: Configure FK in EF model (no cascade)**

```csharp
// In UserConfiguration.cs (or wherever AspNetUsers is configured)
builder.Property(u => u.MergedAt);

builder.HasOne<User>()
    .WithMany()
    .HasForeignKey(u => u.MergedToUserId)
    .OnDelete(DeleteBehavior.Restrict);

builder.HasIndex(u => u.MergedToUserId)
    .HasFilter("\"MergedToUserId\" IS NOT NULL");
```

- [ ] **Step 3: Generate migration**

```
dotnet ef migrations add AddUserMergedToUserId \
    --project src/Humans.Infrastructure \
    --startup-project src/Humans.Web \
    --context HumansDbContext
```

- [ ] **Step 4: EF migration reviewer**

Run the EF migration reviewer agent (`.claude/agents/ef-migration-reviewer.md`) against the generated migration. Mandatory gate per CLAUDE.md.

- [ ] **Step 5: Build and run domain + application tests**

```
dotnet build Humans.slnx -v quiet
dotnet test tests/Humans.Domain.Tests/Humans.Domain.Tests.csproj -v quiet
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet
```

Expected: green, no new failures.

- [ ] **Step 6: Commit**

```
chore(account-merge): add User.MergedToUserId and MergedAt for fold-into-target

Phase 0 of fold redesign. Schema additions only — nullable Guid FK to
AspNetUsers.Id (no cascade) plus nullable Instant. Filtered index on
MergedToUserId WHERE NOT NULL so the chain-follow lookup primitive
GetMergedSourceIdsAsync can find tombstones cheaply.

Spec: docs/superpowers/specs/2026-04-30-account-merge-fold-redesign.md
```

### Task 0.2: Add `IUserService.GetMergedSourceIdsAsync` (chain-follow primitive)

**Files:**
- Modify: `src/Humans.Application/Interfaces/Users/IUserService.cs`
- Modify: `src/Humans.Application/Services/Users/UserService.cs`
- Modify: `src/Humans.Application/Interfaces/Repositories/IUserRepository.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Users/UserRepository.cs`
- Test: `tests/Humans.Application.Tests/Services/Users/UserService_GetMergedSourceIdsTests.cs`

- [ ] **Step 1: Add method to `IUserRepository`**

```csharp
/// <summary>
/// Returns every user id whose <c>MergedToUserId</c> equals
/// <paramref name="targetUserId"/>. Used by the chain-follow lookup
/// (<see cref="IUserService.GetMergedSourceIdsAsync"/>) so reads of
/// append-only rows for the target also surface rows attributed to merged
/// tombstones.
/// </summary>
Task<IReadOnlyList<Guid>> GetMergedSourceIdsAsync(
    Guid targetUserId, CancellationToken ct = default);
```

- [ ] **Step 2: Implement in `UserRepository`**

```csharp
public async Task<IReadOnlyList<Guid>> GetMergedSourceIdsAsync(
    Guid targetUserId, CancellationToken ct = default)
{
    using var db = _factory.CreateDbContext();
    return await db.Users
        .AsNoTracking()
        .Where(u => u.MergedToUserId == targetUserId)
        .Select(u => u.Id)
        .ToListAsync(ct);
}
```

- [ ] **Step 3: Add to `IUserService`**

```csharp
/// <summary>
/// Returns the set of source-tombstone ids whose <c>MergedToUserId</c>
/// equals <paramref name="targetUserId"/>. Single canonical chain-follow
/// primitive: AuditLog, Consent, BudgetAuditLog reads call this rather
/// than each section reinventing the lookup. Set is small (typically
/// zero, usually one).
/// </summary>
Task<IReadOnlySet<Guid>> GetMergedSourceIdsAsync(
    Guid targetUserId, CancellationToken ct = default);
```

- [ ] **Step 4: Implement in `UserService`**

```csharp
public async Task<IReadOnlySet<Guid>> GetMergedSourceIdsAsync(
    Guid targetUserId, CancellationToken ct = default)
{
    var ids = await _repository.GetMergedSourceIdsAsync(targetUserId, ct);
    return ids.ToHashSet();
}
```

- [ ] **Step 5: Decrement IUserService budget (now 31 → 32 with this addition)**

This is the FIRST of the four IUserService additions. After all four land in this PR, the net delta is +4. We'll adjust budget at the end of Phase 3 once we've also identified the four removals (`/audit-surface`). For this commit, raise temporarily ONLY if the budget test fails — we'll bring it back to baseline + balanced delta.

**STOP CONDITION:** If after Phase 3 we cannot identify 4 removable methods on IUserService, STOP and ask Peter. Do not raise the budget permanently.

- [ ] **Step 6: Add unit test**

```csharp
namespace Humans.Application.Tests.Services.Users;

public class UserService_GetMergedSourceIdsTests
{
    [HumansFact]
    public async Task ReturnsTombstoneIds_WhenMergedToUserIdMatches()
    {
        // Arrange: two source users tombstoned to the target.
        var target = await SeedUser();
        var source1 = await SeedUser(mergedTo: target.Id);
        var source2 = await SeedUser(mergedTo: target.Id);
        var unrelated = await SeedUser();

        // Act
        var result = await Service.GetMergedSourceIdsAsync(target.Id);

        // Assert
        result.Should().BeEquivalentTo([source1.Id, source2.Id]);
    }

    [HumansFact]
    public async Task ReturnsEmpty_WhenNoTombstones()
    {
        var target = await SeedUser();
        var result = await Service.GetMergedSourceIdsAsync(target.Id);
        result.Should().BeEmpty();
    }
}
```

- [ ] **Step 7: Build, test, commit**

```
feat(users): add GetMergedSourceIdsAsync chain-follow primitive

Phase 0 of fold redesign. Single canonical lookup: AuditLog, Consent,
BudgetAuditLog reads call this so per-user filters can union source
tombstone ids without each section reinventing the query.
```

---

## Phase 1 — Profile-Section Internal Services

These services live in the same section as `AccountMergeService` so dependency direction is clean.

### Pattern: Reassign on a single table (apply to Tasks 1.1–1.4 below)

Each task follows this shape. The body steps are written once here; subsequent tasks reference it.

**1. Add to repo interface:**

```csharp
/// <summary>
/// Bulk-moves [TableName] rows from <paramref name="sourceUserId"/> to
/// <paramref name="targetUserId"/>, applying the section's conflict rule
/// (see service docstring). Stamps <see cref="ITimestampedEntity.UpdatedAt"/>
/// to <paramref name="updatedAt"/> on moved rows. Returns the count of
/// rows ultimately attributed to the target after dedup.
/// </summary>
Task<int> ReassignToUserAsync(
    Guid sourceUserId, Guid targetUserId, Instant updatedAt,
    CancellationToken ct = default);
```

**2. Implement in repo:**

```csharp
public async Task<int> ReassignToUserAsync(
    Guid sourceUserId, Guid targetUserId, Instant updatedAt,
    CancellationToken ct = default)
{
    using var db = _factory.CreateDbContext();

    // Step 1: load both sides for dedup logic
    var sourceRows = await db.[Set]
        .Where(x => x.UserId == sourceUserId)
        .ToListAsync(ct);
    var targetKeys = await db.[Set]
        .Where(x => x.UserId == targetUserId)
        .Select(x => [conflict-key projection])
        .ToListAsync(ct);

    // Step 2: drop source rows that conflict with existing target rows
    var conflicting = sourceRows.Where(s => targetKeys.Contains([s key])).ToList();
    if (conflicting.Count > 0) db.[Set].RemoveRange(conflicting);

    // Step 3: re-FK the survivors
    foreach (var row in sourceRows.Except(conflicting))
    {
        row.UserId = targetUserId;
        row.UpdatedAt = updatedAt; // or row's timestamp field if non-standard
    }

    await db.SaveChangesAsync(ct);
    return await db.[Set].CountAsync(x => x.UserId == targetUserId, ct);
}
```

**3. Add to service interface + service impl** (passes through to repo + invalidates the section's cache).

**4. Build + test + commit per service.**

### Task 1.1: `IUserEmailService.ReassignToUserAsync`

**Conflict rule:** OR-combine `IsVerified`. Target's `IsPrimary` and `IsGoogle` win. Same `NormalizedAddress` collapses to a single row keyed at target.

**Files:**
- Modify: `src/Humans.Application/Interfaces/Profiles/IUserEmailService.cs`
- Modify: `src/Humans.Application/Services/Profiles/UserEmailService.cs`
- Modify: `src/Humans.Application/Interfaces/Repositories/IUserEmailRepository.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Profiles/UserEmailRepository.cs`

- [ ] **Step 1: Add to repo (custom merge logic — see below)**

```csharp
public async Task<int> ReassignToUserAsync(
    Guid sourceUserId, Guid targetUserId, Instant updatedAt,
    CancellationToken ct = default)
{
    using var db = _factory.CreateDbContext();

    var sourceRows = await db.UserEmails
        .Where(e => e.UserId == sourceUserId).ToListAsync(ct);
    var targetByAddress = await db.UserEmails
        .Where(e => e.UserId == targetUserId)
        .ToDictionaryAsync(e => e.NormalizedAddress, ct);

    foreach (var src in sourceRows)
    {
        if (targetByAddress.TryGetValue(src.NormalizedAddress, out var tgt))
        {
            // Same address on both. OR-combine IsVerified; keep target IsPrimary/IsGoogle.
            tgt.IsVerified = tgt.IsVerified || src.IsVerified;
            tgt.UpdatedAt = updatedAt;
            db.UserEmails.Remove(src);
        }
        else
        {
            // Move; clear primary/google so target's choices stay authoritative.
            src.UserId = targetUserId;
            src.IsPrimary = false;
            src.IsGoogle = false;
            src.UpdatedAt = updatedAt;
        }
    }

    await db.SaveChangesAsync(ct);
    return await db.UserEmails.CountAsync(e => e.UserId == targetUserId, ct);
}
```

- [ ] **Step 2: Add to service interface + impl** (cache invalidation for both source + target).

- [ ] **Step 3: Build + commit**

```
feat(user-emails): add ReassignToUserAsync for account-merge fold

Phase 1 of fold redesign. OR-combines IsVerified on same-address; target
keeps IsPrimary/IsGoogle.
```

### Task 1.2: `IProfileService.ReassignSubAggregatesToUserAsync`

**Conflict rule:** Move VolunteerHistory entries (dedup on `(year, role)`); move Languages (dedup keep highest proficiency); leave the source `Profile` row in place but anonymize its DisplayName/Picture/etc. fields (this is the merge of today's `_profileRepository.AnonymizeForMergeByUserIdAsync` into the new method).

**Budget pressure: IProfileService at 41/41 — need to remove ONE method.**

- [ ] **Step 1: Run `/audit-surface IProfileService` and identify a removable method.** Document the candidate in the commit message. STOP if none found.

- [ ] **Step 2: Implement repo method** (LINQ-free at the service layer).

- [ ] **Step 3: Add to service interface + impl.**

- [ ] **Step 4: Decrement IProfileService budget from 41 → 41** (added 1, removed 1).

- [ ] **Step 5: Build + commit.**

### Task 1.3: `IContactFieldService.ReassignToUserAsync`

**Conflict rule:** dedup on `(Type, Value)` — drop source's row if target has the same.

Apply pattern. Build + commit.

### Task 1.4: `ICommunicationPreferenceService.ReassignToUserAsync`

**Conflict rule:** same key on both → keep most-recent `UpdatedAt`.

Apply pattern. Build + commit.

---

## Phase 2 — Cross-Section Services, Non-Budgeted

These can be parallelized via `superpowers:dispatching-parallel-agents` (max 3 in parallel). Each is independent.

### Task 2.1: `ITicketSyncService.ReassignToUserAsync`

**Conflict rule:** plain re-FK (no same-key conflicts; tickets are unique per purchase).

### Task 2.2: `IRoleAssignmentService.ReassignToUserAsync`

**Conflict rule:** same active role on both → keep target's, drop source's.

Spec note: "already on the interface" — verify with `reforge` (find members on `IRoleAssignmentService`). If a method named `ReassignToUserAsync` already exists, audit its semantics against the spec and reuse; otherwise add.

### Task 2.3: `IShiftSignupService.ReassignToUserAsync`

**Conflict rule:** plain re-FK; shift signups are unique per slot.

### Task 2.4: `IGeneralAvailabilityService.ReassignToUserAsync`

**Conflict rule:** target wins on `(eventYear, userId)` collision.

### Task 2.5: `INotificationService.ReassignRecipientsToUserAsync`

**Conflict rule:** plain re-FK on `notification_recipients.UserId`. Drop source's per-notification collision. Parent `Notification` row is shared, not duplicated.

### Task 2.6: `ICampaignService.ReassignGrantsToUserAsync`

**Conflict rule:** dedup per campaign — if target already has a grant on that campaign, drop source's.

### Task 2.7: `IApplicationDecisionService.ReassignApplicationsToUserAsync`

**Conflict rule:** plain re-FK (historical applications, no dedup).

### Task 2.8: `IFeedbackService.ReassignToUserAsync`

**Conflict rule:** move `FeedbackReport` + `FeedbackMessage` (authorship transfers).

---

## Phase 3 — Cross-Section Services, Budgeted

For each, run `/audit-surface <Interface>` BEFORE adding the method. Identify a clear removable method. STOP if none found.

### Task 3.1: `ITeamService.ReassignToUserAsync` (budget 71)

**Conflict rule:** combine current Add/Remove dance + TeamJoinRequest move. System teams are skipped (managed automatically). For TeamMember: add target on each non-system team source belongs to (skipping where target already a member); remove source. For TeamJoinRequest: move; if target already has an active request to the same team, drop source's.

This is a substantial method body — replaces the current inline foreach in `AcceptAsync`.

Steps:
- [ ] `/audit-surface ITeamService`. Identify removable method.
- [ ] Implement repo helper if needed (or compose existing AddMember/RemoveMember/CancelJoinRequest into the service body).
- [ ] Add to service interface + impl.
- [ ] Decrement budget by 1 (net +1 −1 = 0).
- [ ] Build + commit.

### Task 3.2: `IShiftManagementService.ReassignProfilesAndTagPrefsToUserAsync` (budget 50)

**Conflict rule:** target wins on `(eventYear, userId)` for both `volunteer_event_profiles` and `volunteer_tag_preferences`.

Steps mirror Task 3.1.

### Task 3.3: `ICampService.ReassignAssignmentsToUserAsync` (budget 55)

**Conflict rule:** move `CampLead` + `CampRoleAssignment`; same `(campId, userId, role)` on both → keep target's, drop source's.

Steps mirror Task 3.1.

### Task 3.4: IUserService method additions (budget 31, biggest pressure: −4)

This is the highest-risk task in the plan. Run `/audit-surface IUserService` BEFORE writing any method. Need to identify FOUR removable methods. Candidates worth investigating (without committing — audit-surface decides):

- `BackfillNobodiesTeamGoogleEmailsAsync` (one-shot backfill — used by job?)
- `BackfillParticipationsAsync` (one-shot admin backfill)
- The two count methods (`GetPendingDeletionCountAsync`, `GetRejectedGoogleEmailCountAsync`) — possibly foldable into a single counts API
- `GetByEmailOrAlternateAsync` — may overlap with `IUserEmailService` post-decoupling
- The TrySetGoogleEmail* / SetGoogleEmail* family — possible consolidation

If `/audit-surface` cannot identify 4 dead/redundant methods, **STOP and post a status comment on the PR**.

Then add the four:

- [ ] **Step A: `IUserService.AnonymizeForMergeAsync`** — wraps the existing `IUserRepository.AnonymizeForMergeAsync`, takes `(sourceUserId, targetUserId, now, ct)`, sets `MergedToUserId` and `MergedAt` in the same call, returns `Task<bool>` (true if user existed and was tombstoned). Removes the §9 violation from `AccountMergeService → IUserRepository`.

- [ ] **Step B: `IUserService.ReassignLoginsToUserAsync`** — bulk update `AspNetUserLogins.UserId`, dropping source rows where `(LoginProvider, ProviderKey)` already exists on target. Pattern matches Phase 1 example. Repo method on `IUserRepository`.

- [ ] **Step C: `IUserService.ReassignEventParticipationToUserAsync`** — move `event_participations`; on `(year, userId)` collision keep highest-status row per the `ParticipationStatus` enum precedence (`Attended > Ticketed > NoShow > NotAttending`).

- [ ] **Step D: `IUserService.GetMergedSourceIdsAsync`** — already added in Phase 0 Task 0.2.

- [ ] **Step E: Decrement budget after removals** — set to `31` if 4 removed and 4 added.

- [ ] **Step F: Build + run all tests + commit.**

---

## Phase 4 — Chain-Follow Read Paths

Three immutable-row sections. For each, the read pattern is:

```csharp
public async Task<IReadOnlyList<X>> GetForUserAsync(Guid userId, CancellationToken ct)
{
    var sourceIds = await _userService.GetMergedSourceIdsAsync(userId, ct);
    if (sourceIds.Count == 0)
    {
        return await _repo.GetByUserIdAsync(userId, ct);
    }

    var allIds = sourceIds.Append(userId).ToList();
    return await _repo.GetByUserIdsAsync(allIds, ct);
}
```

Each section's repo gets a `GetByUserIdsAsync(IReadOnlyCollection<Guid>, CancellationToken)` overload (or extends an existing one). The service is the one that does the chain-follow union.

### Task 4.1: `IAuditLogService` chain-follow

**Files:**
- Modify: `src/Humans.Application/Services/AuditLog/AuditLogService.cs`
- Modify: `src/Humans.Application/Interfaces/Repositories/IAuditLogRepository.cs` (add `GetByUserIdsAsync` if missing)
- Modify: `src/Humans.Infrastructure/Repositories/AuditLog/AuditLogRepository.cs`

Update every `Get…ByUserAsync` style read on the service. Inject `IUserService` (one-direction call from AuditLog → Users; foundational direction is preserved per `feedback_user_profile_foundational`).

- [ ] Build + commit.

### Task 4.2: `IConsentService` chain-follow

Update per-user reads + the `IUserDataContributor.ContributeForUserAsync` method.

### Task 4.3: `IBudgetService` chain-follow

Update BudgetAuditLog per-user reads only. Other Budget read paths are unaffected.

---

## Phase 5 — Orchestrator Rewrite

### Task 5.1: Rewrite `AccountMergeService.AcceptAsync`

**Files:**
- Modify: `src/Humans.Application/Services/Profile/AccountMergeService.cs`

- [ ] **Step 1: Update constructor / injected services**

Drop `_userEmailRepository`, `_userRepository`, `_profileRepository` if they were only used by `AcceptAsync`. Verify with `reforge` — `RejectAsync` and others may still need them (RejectAsync uses `_userEmailRepository.RemoveByIdAsync`, keep). Add the new section service interfaces to ctor.

- [ ] **Step 2: Replace `AcceptAsync` body**

```csharp
public async Task AcceptAsync(
    Guid requestId, Guid adminUserId,
    string? notes = null, CancellationToken ct = default)
{
    var request = await _mergeRepository.GetByIdAsync(requestId, ct)
        ?? throw new InvalidOperationException("Merge request not found.");

    if (request.Status != AccountMergeRequestStatus.Pending)
        throw new InvalidOperationException("Merge request is not pending.");

    var now = _clock.GetCurrentInstant();
    var sourceId = request.SourceUserId;
    var targetId = request.TargetUserId;

    _logger.LogInformation(
        "Admin {AdminId} accepting merge request {RequestId}: folding {SourceUserId} into {TargetUserId}",
        adminUserId, requestId, sourceId, targetId);

    try
    {
        using (var scope = new TransactionScope(
            TransactionScopeOption.Required,
            new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
            TransactionScopeAsyncFlowOption.Enabled))
        {
            // 1. Profile-section internal
            await _userEmailService.ReassignToUserAsync(sourceId, targetId, now, ct);
            await _profileService.ReassignSubAggregatesToUserAsync(sourceId, targetId, now, ct);
            await _contactFieldService.ReassignToUserAsync(sourceId, targetId, now, ct);
            await _communicationPreferenceService.ReassignToUserAsync(sourceId, targetId, now, ct);

            // 2. Cross-section
            await _userService.ReassignLoginsToUserAsync(sourceId, targetId, now, ct);
            await _userService.ReassignEventParticipationToUserAsync(sourceId, targetId, now, ct);
            await _ticketSyncService.ReassignToUserAsync(sourceId, targetId, now, ct);
            await _roleAssignmentService.ReassignToUserAsync(sourceId, targetId, now, ct);
            await _teamService.ReassignToUserAsync(sourceId, targetId, adminUserId, now, ct);
            await _shiftSignupService.ReassignToUserAsync(sourceId, targetId, now, ct);
            await _shiftManagementService.ReassignProfilesAndTagPrefsToUserAsync(sourceId, targetId, now, ct);
            await _generalAvailabilityService.ReassignToUserAsync(sourceId, targetId, now, ct);
            await _notificationService.ReassignRecipientsToUserAsync(sourceId, targetId, now, ct);
            await _campaignService.ReassignGrantsToUserAsync(sourceId, targetId, now, ct);
            await _campService.ReassignAssignmentsToUserAsync(sourceId, targetId, now, ct);
            await _applicationDecisionService.ReassignApplicationsToUserAsync(sourceId, targetId, now, ct);
            await _feedbackService.ReassignToUserAsync(sourceId, targetId, now, ct);

            // 3. Verify the pending email on the target
            var verified = await _userEmailRepository.MarkVerifiedAsync(request.PendingEmailId, now, ct);
            if (!verified)
                throw new InvalidOperationException(
                    $"Pending email {request.PendingEmailId} no longer exists. Cannot complete merge.");

            // 4. Tombstone source User row (sets MergedToUserId, MergedAt, locks out)
            await _userService.AnonymizeForMergeAsync(sourceId, targetId, now, ct);

            // 5. Mark merge request Accepted + audit
            request.Status = AccountMergeRequestStatus.Accepted;
            request.ResolvedAt = now;
            request.ResolvedByUserId = adminUserId;
            request.AdminNotes = notes;
            await _mergeRepository.UpdateAsync(request, ct);

            await _auditLogService.LogAsync(
                AuditAction.AccountMergeAccepted,
                nameof(AccountMergeRequest), request.Id,
                $"Folded source {sourceId} into target {targetId} — email: {request.Email}",
                adminUserId,
                relatedEntityId: targetId, relatedEntityType: nameof(User));

            scope.Complete();
        }

        // Post-commit cache invalidations
        await _fullProfileInvalidator.InvalidateAsync(sourceId, ct);
        await _fullProfileInvalidator.InvalidateAsync(targetId, ct);
        _teamService.RemoveMemberFromAllTeamsCache(sourceId);
    }
    finally
    {
        _teamService.InvalidateActiveTeamsCache();
    }
}
```

- [ ] **Step 3: Build + run all unit/application tests + commit**

```
feat(account-merge): rewrite AcceptAsync as fold-into-target

Phase 5 of fold redesign. Source data re-FKs to target via section
service Reassign methods; source User row is tombstoned with
MergedToUserId + MergedAt. Single TransactionScope covers all
cross-section writes; existing post-commit cache invalidations preserved.

Spec: docs/superpowers/specs/2026-04-30-account-merge-fold-redesign.md
```

---

## Phase 6 — Tests

### Task 6.1: Test fixture helpers

**File:** `tests/Humans.IntegrationTests/AccountMerge/MergeFixtureExtensions.cs`

Single helper class that seeds source + target Users with a `Profile` each, plus a single per-section seeder method per section. Used by both `AcceptAsyncFoldTests` and `AcceptAsyncFullFixtureTest`.

```csharp
public static class MergeFixtureExtensions
{
    public static async Task<(Guid sourceId, Guid targetId)> SeedMergeFixtureAsync(
        this IntegrationTestFixture fx, Action<MergeFixtureBuilder>? configure = null)
    {
        var sourceId = await fx.SeedUserAsync(displayName: "Source");
        var targetId = await fx.SeedUserAsync(displayName: "Target");

        var builder = new MergeFixtureBuilder(fx, sourceId, targetId);
        configure?.Invoke(builder);
        await builder.SaveAllAsync();

        return (sourceId, targetId);
    }

    // Per-section seeder methods on MergeFixtureBuilder, e.g.:
    // builder.WithSourceTickets(count: 2)
    // builder.WithTargetTickets(count: 1)
    // builder.WithSourceContactField(ContactType.Phone, "+34 999 111 111")
    // builder.WithTargetContactField(ContactType.Phone, "+34 999 111 111")
    //   ↑ same value on both — exercises the dedup rule
}
```

- [ ] Build + commit.

### Task 6.2: Reassign per-section tests

**File:** `tests/Humans.IntegrationTests/AccountMerge/AcceptAsyncFoldTests.cs`

One test per re-FK rule per the spec §"Test coverage" (22 tests). Pattern per test:

```csharp
[HumansFact]
public async Task AcceptAsync_UserEmails_OrCombinesFlags_KeepsTargetPrimaryAndGoogle()
{
    // Arrange
    var (sourceId, targetId) = await Fixture.SeedMergeFixtureAsync(b =>
    {
        b.WithSourceEmail("shared@example.com", verified: true, isPrimary: false, isGoogle: false);
        b.WithTargetEmail("shared@example.com", verified: false, isPrimary: true, isGoogle: true);
    });
    var requestId = await Fixture.SeedMergeRequestAsync(sourceId, targetId);

    // Act
    await Fixture.AccountMergeService.AcceptAsync(
        requestId, Fixture.AdminUserId, ct: TestContext.Current.CancellationToken);

    // Assert
    var emails = await Fixture.UserEmails.ForUserAsync(targetId);
    var collapsed = emails.Should().ContainSingle(e => e.NormalizedAddress == "shared@example.com").Subject;
    collapsed.IsVerified.Should().BeTrue("OR-combine across both rows");
    collapsed.IsPrimary.Should().BeTrue("target's primary is preserved");
    collapsed.IsGoogle.Should().BeTrue("target's google flag is preserved");
    emails.Should().NotContain(e => e.UserId == sourceId);
}
```

Tests to write (one method each — names map to spec §"Test coverage"):

1. `AcceptAsync_UserEmails_OrCombinesFlags_KeepsTargetPrimaryAndGoogle`
2. `AcceptAsync_UserEmails_CollapsesSameEmail`
3. `AcceptAsync_AspNetUserLogins_ReFKs_DropsSameKey`
4. `AcceptAsync_Profile_AnonymizesAndKeepsTombstoneRow`
5. `AcceptAsync_ContactFields_Move_DedupOnTypeValue`
6. `AcceptAsync_VolunteerHistory_Move_DedupIdenticalEntries`
7. `AcceptAsync_Languages_Move_DedupKeepHighestProficiency`
8. `AcceptAsync_CommunicationPreferences_MostRecentWins`
9. `AcceptAsync_EventParticipation_HighestStatusWins_ByEnumPrecedence`
10. `AcceptAsync_Tickets_ReFK`
11. `AcceptAsync_RoleAssignments_ReFKs_DropsSameKey`
12. `AcceptAsync_TeamMembers_AddTargetRemoveSource_NonSystemOnly`
13. `AcceptAsync_TeamJoinRequests_Move_DropDuplicateActive`
14. `AcceptAsync_ShiftSignups_PlainReFK`
15. `AcceptAsync_VolunteerEventProfiles_AndTagPrefs_Move_TargetWinsOnCollision`
16. `AcceptAsync_GeneralAvailability_Move_TargetWinsOnCollision`
17. `AcceptAsync_NotificationRecipients_Move_DropDuplicate`
18. `AcceptAsync_CampaignGrants_Move_DedupPerCampaign`
19. `AcceptAsync_CampLeadAndRoleAssignments_Move_DedupPerRole`
20. `AcceptAsync_Applications_Move_AllHistorical`
21. `AcceptAsync_FeedbackReportsAndMessages_Move`
22. `AcceptAsync_AuditLog_NotMutated_StaysAtSourceId` (assertion: source still owns the historical audit row)
23. `AcceptAsync_ConsentRecords_NotMutated_StaysAtSourceId`
24. `AcceptAsync_BudgetAuditLog_NotMutated_StaysAtSourceId`
25. `AcceptAsync_TombstonesSourceWithMergedToUserId`
26. `AcceptAsync_PreventsSourceLogin` (assertion: source `LockoutEnd` is far-future)

- [ ] Build + commit (single commit for the test class — they're cohesive).

### Task 6.3: Chain-follow read tests

**File:** `tests/Humans.IntegrationTests/AccountMerge/ChainFollowReadTests.cs`

```csharp
[HumansFact]
public async Task AuditLog_ReadByUserId_FollowsMergedToUserIdChain()
{
    var (sourceId, targetId) = await Fixture.SeedMergeFixtureAsync(b => { /* none */ });
    await Fixture.AuditLog.LogAsync(AuditAction.SomeAction, "X", Guid.NewGuid(), "before merge", sourceId);
    var requestId = await Fixture.SeedMergeRequestAsync(sourceId, targetId);
    await Fixture.AccountMergeService.AcceptAsync(requestId, Fixture.AdminUserId);

    var entries = await Fixture.AuditLog.GetForUserAsync(targetId);
    entries.Should().Contain(e => e.UserId == sourceId);
}

[HumansFact]
public async Task ConsentExport_ForTarget_IncludesSourceTombstoneRecords() { /* … */ }

[HumansFact]
public async Task BudgetAuditLog_ReadByUserId_FollowsMergedToUserIdChain() { /* … */ }
```

- [ ] Build + commit.

### Task 6.4: Full-fixture integration test

**File:** `tests/Humans.IntegrationTests/AccountMerge/AcceptAsyncFullFixtureTest.cs`

Single test that seeds source with rows on every section, accepts the merge, asserts:
- Source has zero rows in any cross-section live table (queries each section's `WhereUserId(sourceId)` and asserts `0`)
- Target has all source-originated live data
- Source User row has `MergedToUserId == targetId`, `MergedAt != null`, `LockoutEnd > 100 years out`
- Append-only sections (audit_log, consent_records, budget_audit_log) still have their source-owned rows untouched

- [ ] Build + commit.

---

## Phase 7 — Doc + Cleanup

### Task 7.1: Delete dead anonymize-source code

For each, verify with `reforge` (find references) that the only caller was the old `AcceptAsync` body. Delete and update docs.

- [ ] `IUserRepository.RemoveExternalLoginsAsync` (if no other caller)
- [ ] `IUserEmailRepository.RemoveAllForUserAndSaveAsync` (if no other caller)
- [ ] `IProfileRepository.AnonymizeForMergeByUserIdAsync` (folded into `IProfileService.ReassignSubAggregatesToUserAsync`)
- [ ] `IUserRepository.AnonymizeForMergeAsync` — keep if other callers; otherwise replaced by service-level version. (`ApplyExpiredDeletionAnonymizationAsync` is a different flow — verify.)
- [ ] `IRoleAssignmentService.RevokeAllActiveAsync` if no other caller (likely still used by GDPR purge path).

### Task 7.2: Update section invariant docs

Per the File Map list. For each section that gained a `Reassign…` method: add to the section's `## Triggers` (when account merge accepts, `Reassign…` is invoked) and `## Cross-Section Dependencies` (called by Profile section's `IAccountMergeService` for merges).

For the three immutable sections: note the chain-follow read rule under `## Invariants`.

- [ ] Build + commit.

### Task 7.3: Update `data-model.md`

Append `User.MergedToUserId` and `User.MergedAt` to the User entity table; add a cross-cutting note for chain-follow on append-only sections.

- [ ] Build + commit.

### Task 7.4: Run full test suite

```
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
```

Integration tests have known flakes — re-run any single failure once before treating as a regression.

- [ ] Verify green. Commit any final fixups.

---

## Phase 8 — Push & Open PR

- [ ] **Push the branch:**

```
git push -u origin feat/account-merge-fold-redesign
```

- [ ] **Open the PR:**

```
gh pr create --repo peterdrier/Humans --base main --title "feat(account-merge): fold-into-target redesign of AcceptAsync" --body "..."
```

PR body skeleton:

```markdown
## Summary

Replaces the anonymize-source `AccountMergeService.AcceptAsync` body with a fold-into-target model. Source User row becomes a tombstone (`MergedToUserId`, `MergedAt`); cross-section data re-FKs to target with per-section conflict rules; immutable rows (AuditLog, Consent, BudgetAuditLog) stay at source and surface for target via `IUserService.GetMergedSourceIdsAsync` chain-follow.

Implements task 18 of the email-and-OAuth decoupling sequence (deferred from `peterdrier#376`).

**Spec:** `docs/superpowers/specs/2026-04-30-account-merge-fold-redesign.md`
**Parent sequence:** `docs/superpowers/specs/2026-04-27-email-and-oauth-decoupling-design.md`

## Schema

Additive only:
- `AspNetUsers.MergedToUserId` — nullable Guid FK to `AspNetUsers.Id`, no cascade
- `AspNetUsers.MergedAt` — nullable Instant
- Filtered index on `MergedToUserId WHERE NOT NULL`

No drops.

## Section service surface

| Section | Method added | Conflict rule |
| ... full table from spec ... |

## Test plan

- [ ] All 22 per-rule integration tests
- [ ] 3 chain-follow read tests
- [ ] 1 full-fixture integration test
- [ ] Tombstone + lockout assertions

## Interface budget movements

- `IUserService` 31 → 31 (added 4, removed 4)
- `IProfileService` 41 → 41 (added 1, removed 1)
- `ITeamService` 71 → 71 (added 1, removed 1)
- `IShiftManagementService` 50 → 50
- `ICampService` 55 → 55

(Justifications noted in commits.)
```

- [ ] **Final build/test pass + check CI green.**

---

## Phase 9 — Address Review

Per `feedback_codex_thread_replies` and `feedback_done_means_codex_clean`:

- [ ] On every push, Codex + Claude review automatically. Do NOT ping (`feedback_no_ping_pr_reviewers`).
- [ ] Pull review comments via `gh api repos/peterdrier/Humans/pulls/{n}/comments` (and check `nobodies-collective/Humans` too — `feedback_pr_review_both_repos`).
- [ ] Reply to each in-thread via `/pulls/{n}/comments/{id}/replies` (not top-level).
- [ ] Fix every finding (critical + important + minor).
- [ ] Wait for fresh Codex pass to be clean.
- [ ] When done: PR body checklist all checked, all threads responded.

---

## Self-Review (post-write check)

- ✅ Spec coverage: every method in the spec table has a task; every test in the spec test list has a task.
- ✅ Type consistency: `Reassign…ToUserAsync` signature consistent across phases (sourceUserId, targetUserId, [actorUserId for ITeamService only], updatedAt, ct).
- ✅ Stop conditions documented at the budget-pressure tasks.
- ⚠️ Code-block expansion: per-section repo bodies for non-budgeted sections are templated rather than written verbatim. This is a deliberate compression for a 16-section plan; the PATTERN block in Phase 1 establishes the shape. Subagents should read that pattern + the section's entity model + apply.

