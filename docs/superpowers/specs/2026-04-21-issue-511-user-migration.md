# Issue #511 — User section §15 migration (redo)

**Date:** 2026-04-21
**Status:** Draft — awaiting approval before implementation
**Scope:** Migrate the User section to the §15 pattern (per `docs/architecture/design-rules.md` §15). Closes upstream issue #511. Supersedes the abandoned PR #236 branch, which was written against the pre-rework PR #235 shape and cannot be mechanically rebased onto current `main`.

## Background

PR #236 (`sprint/2026-04-17/issue-511`) was branched off the original PR #235 before its cache-collapse rework (`docs/superpowers/specs/2026-04-20-pr235-cache-collapse-design.md`). Every one of its 13 User-specific commits assumes infrastructure that no longer exists on `main`:

- `IProfileStore` / `ProfileStoreWarmupHostedService` — deleted in merged #235
- `IVolunteerHistoryRepository` / `VolunteerHistoryService` — deleted in merged #235 (CV entries are now sub-aggregate through `IProfileRepository.ReconcileCVEntriesAsync`)
- Old-shape `CachingProfileService` — replaced by the collapsed-dict shape with `IFullProfileInvalidator`

A mechanical rebase hits conflicts on almost every commit, and even if those were resolved the result would use the pre-rework pattern (separate `IUserStore` + `UserStoreWarmupHostedService` + Scrutor `Decorate<>`) that §15 explicitly replaced.

Restart clean off current `main`.

## Survey result — scope is smaller than PR #236's diff

`reforge references` against `origin/main` (commit `b08e36bf`) shows that every cross-domain navigation property on `User` has **zero external references**:

| Nav property | External refs on main |
|---|---|
| `User.Profile` | 0 |
| `User.UserEmails` | 0 (1 internal ref inside `User.GetEffectiveEmail()`) |
| `User.TeamMemberships` | 0 |
| `User.RoleAssignments` | 0 |
| `User.Applications` | 0 |
| `User.ConsentRecords` | 0 |
| `User.CommunicationPreferences` | 0 |
| `User.GetEffectiveEmail()` | 0 |

PR #235's rework incidentally migrated every consumer when it moved Profile to the §15 pattern. PR #236's "19 consumers migrated" work is effectively already done on `main`. The remaining work is:

1. New User-section repository infrastructure in the §15 shape.
2. Move `UserService` into `Humans.Application`, strip `DbContext`.
3. Delete dead nav properties + `GetEffectiveEmail()` + relocate cross-domain EF relationship configs out of `UserConfiguration`.
4. Architecture tests pinning the invariants.

## Design decision — Option A: no decorator, no cache, no DTO

Three shapes were considered:

- **Option A — no cache.** Inner `UserService` in Application, Singleton `IUserRepository` backed by `IDbContextFactory`, injects `IFullProfileInvalidator`, calls `InvalidateAsync` after writes that change FullProfile-visible fields (`DisplayName`, `ProfilePictureUrl`, `Email`, `GoogleEmail`).
- **Option B — decorator-for-invalidation-only.** Same as A but the invalidator call lives in a Singleton `CachingUserService` wrapper that forwards reads pass-through. Slight separation-of-concerns win; otherwise identical.
- **Option C — full dict-cache decorator.** Mirror `CachingProfileService` with a `ConcurrentDictionary<Guid, FullUser>` + warmup hosted service + same-instance `IUserCacheInvalidator` alias.

**Option A wins.** Rationale:

- User section is ~500 rows, simple entity shape, no stitched projection, no known hot bulk-read path. `CachingProfileService` exists because `FullProfile` is a heavy stitched projection (Profile + User + CV) used on hot bulk paths (birthday widget, location directory, admin human list, search). User has no analog.
- Governance's caching decorator was **deleted outright** in #242 (`Remove Governance caching layer (store + decorator + warmup)`) because the data was cheap enough that a decorator wasn't earning its keep. The User section is in the same bucket.
- Caching transparency (per user rule: "caching is transparent to the app") is trivially satisfied when there is no cache.
- A future bulk-read path (if one emerges) can add a dict-backed method on the decorator at that time, with a proper projection type.

Adding a dict + warmup + DTO "to match PR #235's shape" is cargo-cult: §15 mandates "services go through their repository and own their data," not "every service must have a caching decorator." The design-rules doc's target pattern diagram (§15) shows the decorator layer as *optional*.

## Cross-section invalidation

`UserService` writes that change fields included in `FullProfile` must invalidate the Profile cache so stale `FullProfile` entries don't linger:

| Write method | FullProfile-visible fields touched | Invalidate? |
|---|---|---|
| `UpdateDisplayNameAsync` | `DisplayName` | ✅ |
| `TrySetGoogleEmailAsync` | `GoogleEmail` (not in FullProfile today, but consumed by `FullProfile.NotificationEmail` fallback logic) | ✅ |
| `SetDeletionPendingAsync` | — | ❌ (FullProfile does not expose deletion state) |
| `ClearDeletionAsync` | — | ❌ |
| `DeclareNotAttendingAsync` / `UndoNotAttendingAsync` / `SetParticipationFromTicketSyncAsync` / `RemoveTicketSyncParticipationAsync` / `BackfillParticipationsAsync` | — (EventParticipation is aggregate-local to User, not in FullProfile) | ❌ |

`UserService` injects `IFullProfileInvalidator` and calls `InvalidateAsync(userId, ct)` at the end of the two affected methods. Matches the existing cross-section invalidation pattern used by `AccountMergeService`, `DuplicateAccountService`, etc.

## Target shape

### Layer layout

```
Controller / other service
  ↓ IUserService                                   (Application interface)
UserService                                        [Application — pure logic]
  ↓ IUserRepository, IFullProfileInvalidator
UserRepository                                     [Infrastructure — EF, IDbContextFactory]
```

No decorator, no warmup hosted service, no dict cache, no DTO.

### New files

- `src/Humans.Application/Interfaces/Repositories/IUserRepository.cs`
- `src/Humans.Infrastructure/Repositories/UserRepository.cs` (Singleton, `IDbContextFactory<HumansDbContext>`-based, `AsNoTracking` for reads, `*ForMutationAsync` convention for load-then-save paths, matches `ProfileRepository` shape)
- `src/Humans.Application/Services/Users/UserService.cs` (moved from `src/Humans.Infrastructure/Services/UserService.cs`; strips `HumansDbContext` and `IClock` direct usage — goes through repo)
- `tests/Humans.Application.Tests/Architecture/UserArchitectureTests.cs`
- `tests/Humans.Application.Tests/Repositories/UserRepositoryTests.cs`

### Changed files

- `src/Humans.Domain/Entities/User.cs` — delete 7 nav properties + `GetEffectiveEmail()`
- `src/Humans.Infrastructure/Data/Configurations/UserConfiguration.cs` — remove 7 `HasOne`/`HasMany` blocks
- `src/Humans.Infrastructure/Data/Configurations/ProfileConfiguration.cs` — add uni-directional relationship config for Profile → User
- `src/Humans.Infrastructure/Data/Configurations/RoleAssignmentConfiguration.cs` — same for RoleAssignment → User
- `src/Humans.Infrastructure/Data/Configurations/ConsentRecordConfiguration.cs` — same for ConsentRecord → User
- `src/Humans.Infrastructure/Data/Configurations/ApplicationConfiguration.cs` — same for Application → User
- `src/Humans.Infrastructure/Data/Configurations/TeamMemberConfiguration.cs` — same for TeamMember → User
- `src/Humans.Infrastructure/Data/Configurations/UserEmailConfiguration.cs` — same for UserEmail → User
- `src/Humans.Infrastructure/Data/Configurations/CommunicationPreferenceConfiguration.cs` — same for CommunicationPreference → User
- `src/Humans.Infrastructure/Migrations/HumansDbContextModelSnapshot.cs` — flips nav-name strings to `null` on each relevant `HasOne/WithMany` block (no column change)
- `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs` — new registrations: `IUserRepository` as Singleton, `UserService` in Application namespace as Scoped (replaces the current Scoped registration of the Infrastructure-namespace `UserService`); `IUserService` forward stays the same shape
- `tests/Humans.Application.Tests/Services/UserServiceTests.cs` — update for repo-backed shape
- `docs/architecture/design-rules.md` §15c — mark User section migrated, note Option A (no decorator) decision
- `docs/sections/` — add `Users.md` invariants doc (or update if present)

### Deleted files

- `src/Humans.Infrastructure/Services/UserService.cs` (replaced by Application-layer version)

## Schema impact

Zero. Removing navigation properties is a pure model change; FK columns (`UserId` on every child table) stay untouched. `dotnet ef migrations add` produces an empty `Up()`/`Down()`, so **no migration file is committed** — only `HumansDbContextModelSnapshot.cs` updates. Same pattern as the PR #236 body documented.

No `[Obsolete]` markers — nothing column-mapped is being removed. Nav properties don't round-trip to schema.

## `User` entity — what stays, what goes

**Delete:**

```csharp
public Profile? Profile { get; set; }
public ICollection<RoleAssignment> RoleAssignments { get; } = new List<RoleAssignment>();
public ICollection<ConsentRecord> ConsentRecords { get; } = new List<ConsentRecord>();
public ICollection<Application> Applications { get; } = new List<Application>();
public ICollection<TeamMember> TeamMemberships { get; } = new List<TeamMember>();
public ICollection<UserEmail> UserEmails { get; } = new List<UserEmail>();
public ICollection<CommunicationPreference> CommunicationPreferences { get; } = new List<CommunicationPreference>();
public string? GetEffectiveEmail() { ... }
```

**Keep:**

```csharp
public ICollection<EventParticipation> EventParticipations { get; } = new List<EventParticipation>();  // aggregate-local to User section
public string? GetGoogleServiceEmail() { ... }  // uses only User.GoogleEmail + User.Email, no nav props
```

Plus all the User field properties (`DisplayName`, `Email`, `GoogleEmail`, `GoogleEmailStatus`, `CreatedAt`, `LastLoginAt`, `DeletionRequestedAt`, `DeletionScheduledFor`, `ContactSource`, `ExternalSourceId`, etc.) — unchanged.

## `IUserService` surface — unchanged

The 16 existing methods stay verbatim. No new methods, no removed methods, no DTO-return variants. The decorator absence means `GetByIdAsync` / `GetByIdsAsync` / `GetAllUsersAsync` stay hitting the repo each time — acceptable at ~500-user scale with `AsNoTracking` + `IDbContextFactory`.

## Architecture tests

New `tests/Humans.Application.Tests/Architecture/UserArchitectureTests.cs`. Pins §15 invariants for User section:

- `UserService` type lives in `Humans.Application.Services.Users` namespace
- `UserService` constructor does **not** take `HumansDbContext`, `IMemoryCache`, or any `Microsoft.EntityFrameworkCore.*` type
- `UserService` constructor takes `IUserRepository`
- `UserService` constructor takes `IFullProfileInvalidator` (proves cross-section invalidation wiring)
- `IUserRepository` registration is Singleton (so the inner service can inject it directly without `IServiceScopeFactory`)
- `User` entity has no navigation properties to: `Profile`, `RoleAssignment`, `ConsentRecord`, `Application`, `TeamMember`, `UserEmail`, `CommunicationPreference`
- `User` entity still has `EventParticipations` navigation (aggregate-local)
- No type in `Humans.Application.*` imports `Microsoft.EntityFrameworkCore`

These invariants are what would otherwise need a base class. Tests > inheritance here: they catch drift at CI time, don't distort the type hierarchy, and work across sections with different shapes (Governance has no decorator, Profile has one, User has neither — all pass the same test suite's structural checks).

## Repository shape

`IUserRepository` mirrors `IProfileRepository`:

```csharp
public interface IUserRepository
{
    // Reads — AsNoTracking, detached entities
    Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
    Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default);
    Task<User?> GetByEmailOrAlternateAsync(string normalizedEmail, CancellationToken ct = default);
    Task<IReadOnlyList<User>> GetContactUsersAsync(string? search, CancellationToken ct = default);

    // Reads for mutation paths — tracked, must be saved via the same repo method
    Task<User?> GetByIdForMutationAsync(Guid userId, CancellationToken ct = default);

    // Writes — repo owns DbContext lifetime; service does not compose mutations
    Task UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default);
    Task<bool> TrySetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default);
    Task<bool> SetDeletionPendingAsync(Guid userId, Instant requestedAt, Instant scheduledFor, CancellationToken ct = default);
    Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default);

    // EventParticipation (aggregate-local)
    Task<EventParticipation?> GetParticipationAsync(Guid userId, int year, CancellationToken ct = default);
    Task<IReadOnlyList<EventParticipation>> GetAllParticipationsForYearAsync(int year, CancellationToken ct = default);
    Task<EventParticipation> DeclareNotAttendingAsync(Guid userId, int year, Instant now, CancellationToken ct = default);
    Task<bool> UndoNotAttendingAsync(Guid userId, int year, CancellationToken ct = default);
    Task SetParticipationFromTicketSyncAsync(Guid userId, int year, ParticipationStatus status, Instant now, CancellationToken ct = default);
    Task RemoveTicketSyncParticipationAsync(Guid userId, int year, CancellationToken ct = default);
    Task<int> BackfillParticipationsAsync(int year, IReadOnlyList<(Guid UserId, ParticipationStatus Status)> entries, Instant now, CancellationToken ct = default);
}
```

`UserService` injects `IUserRepository` + `IClock` + `IFullProfileInvalidator` + `ILogger<UserService>`. Writes that change FullProfile-visible fields call `_fullProfileInvalidator.InvalidateAsync(userId, ct)` at the end.

## Commit phasing

One branch (`sprint/2026-04-21/issue-511-redo`), one PR, separable commits:

1. **Spec** — this file. Gets the PR open on GitHub for review ahead of implementation.
2. **`IUserRepository` + `UserRepository` + tests** — new Singleton repo, no consumer wiring yet. Tests under `tests/Humans.Application.Tests/Repositories/UserRepositoryTests.cs` mirror `ProfileRepositoryTests.cs`.
3. **Move `UserService` → `Humans.Application.Services.Users`** — strip `DbContext`, go through repo, inject `IFullProfileInvalidator`, add invalidation calls. DI wiring updated. Tests updated.
4. **Delete dead `User` nav props + `GetEffectiveEmail()` + relocate EF configs** — snapshot diff confirms no column change.
5. **Architecture tests** under `tests/Humans.Application.Tests/Architecture/UserArchitectureTests.cs`.
6. **Docs** — update `docs/architecture/design-rules.md` §15c; add `docs/sections/Users.md` if not present.

Each commit builds and tests pass before the next.

## Validation gate

- `dotnet build Humans.slnx` — 0 errors, 0 warnings
- `dotnet test Humans.slnx` — all green
- `dotnet format Humans.slnx --verify-no-changes` — clean
- `reforge dbset-usage Humans.Application.Services.Users.UserService` — 0 (no direct DbContext access)
- `grep -r "using Microsoft.EntityFrameworkCore" src/Humans.Application` — 0 hits
- `dotnet ef migrations add VerifyNoSchemaChange` — empty `Up()` / `Down()`, discard the file
- Browser smoke on preview (`{pr}.n.burn.camp`): login flow, profile view, admin human detail, team page avatars, event participation (declare-not-attending via /Vol), account deletion request

## Out of scope

Not this PR:

- **Decorator / dict cache on User.** Explicitly rejected above. If a future hot bulk-read path emerges, add it then with a proper projection type.
- **`OnboardingService` DbContext cleanup.** §15c transitional violation, belongs to the Onboarding section migration.
- **`TeamService` DbContext usage** (64 DbSet accesses today). Teams section migration, its own PR.
- **`AccountMergeService` DbContext direct writes.** Cross-cutting concern with `IUserEmailService` semantics — separate fix.
- **`GoogleWorkspaceSyncService` cleanups** (PR #236 commits 1a032823, d58dd953). Independent architectural improvements, separate PR(s). Will be evaluated after User migration lands.
- **`TeamService ↔ UserEmailService` cycle break** (PR #236 commit 687b8d88). Independent architectural fix, separate PR.

## Why base classes for services / decorators were considered and rejected

Brief summary — the full rationale is in the conversation that produced this spec.

- **Service base class:** contract is already compile-time-enforced (`Humans.Application.csproj` cannot reference `Microsoft.EntityFrameworkCore`). Shared deps are a subset (`IClock`, `ILogger<T>`, a repo); hoisting them to a base adds ceremony without safety. Cross-section invalidator calls are N:M and specific — can't generalize without devolving to empty hooks or a registry.
- **Caching decorator base class:** tempting, but C# single-inheritance means the decorator still implements the section interface (the ~30 method bodies do the real work; the base only saves the dict plumbing). More importantly, a base class that mandates a dict pushes every decorator into the cache-when-you-shouldn't corner (Governance + User both deleted their caches) and erases the "caching — including no caching — is transparent" flexibility.

Enforcement lives in architecture tests, not inheritance. `ProfileArchitectureTests.cs` is the reference; `UserArchitectureTests.cs` and future per-section variants extend it.

## Relationship to PR #236

PR #236 (`sprint/2026-04-17/issue-511`) is **not mergeable** as-is. Close it with a reference to this spec and the new branch (`sprint/2026-04-21/issue-511-redo`) once this PR opens. Commits worth preserving from it (as independent follow-ons, not this PR):

- `687b8d88` — Break `TeamService ↔ UserEmailService` cycle via `IAccountMergeRequestRepository`
- `1a032823` — Drop `GoogleWorkspaceSyncService` lazy `ITeamService` injection
- `d58dd953` — Replace `GoogleWorkspaceSyncService` direct `DbContext.TeamMembers` read with narrow `ITeamMembershipRepository`
- `cdc61d61` — Route `GoogleAdminService` + `TicketQueryService` through `IUserEmailService`
- `9dfa2473` — `IUserEmailService.RemoveUnverifiedEmailAsync`

Each is evaluated on its own merits against current `main` — some may already be unnecessary.
