# PR #235 cache-collapse design

**Date:** 2026-04-20
**Status:** Draft — awaiting final review before implementation plan
**Scope:** Rework PR #235 (`sprint/2026-04-16/issue-504`) to collapse a redundant two-layer cache into a single decorator-owned cache. Establishes the reference pattern for §15 Phases 4–8.

## Background

PR #235 introduces the repo/store/decorator pattern for the Profile section (§15 Step 0, issue #504) as the template for the rest of the service-ownership migration. On review, the caching layer is redundant:

- `IProfileStore` — singleton `ConcurrentDictionary<Guid, CachedProfile>`, warmed at startup by `ProfileStoreWarmupHostedService`, never expires.
- `CachingProfileService._cache` — `IMemoryCache` per-user `Profile` entity, 2-min TTL.

Both caches are per-user. Since the store is always warm, the 2-min `IMemoryCache` adds nothing. The decorator delegates `GetCachedProfile` / `GetCachedProfileAsync` into the inner service, which reads from the store — so the decorator isn't even the reader for the store-backed cache. The design has two layers that overlap in purpose and leak cache responsibility across the store/decorator seam.

The PR's whole reason to exist is being the reference the next five §15 phases will be modelled on. Fixing it now is cheaper than inheriting the redundancy through Phases 4–8.

Separately, `CachedProfile.VolunteerHistory` is dead code: grep confirms zero readers. The one place that seemed to use it (`ProfileCard`) builds its own view model via `IVolunteerHistoryService` and includes the `LocalDate` that the projection dropped. The projection field and its `CachedVolunteerEntry` type serve nothing today.

The pattern resolution is to *revive* the projection with the missing date rather than delete it: fold `IVolunteerHistoryService` into `IProfileService`, include CV entries properly in the projection, and let `ProfileCard` and all other consumers read from the cached shape. This deletes one service, one repository, one decorator, and their tests — the sub-aggregate-in-parent rule below generalises this for the rest of §15.

## Goal

One cache, owned by the decorator, for the Profile section. Decorator is a pure optimization: removing it leaves the application fully functional, just slower. The pattern documented in `docs/architecture/design-rules.md` §15 matches the new shape and becomes the template Phases 4–8 follow without modification.

## Non-goals

- Governance section rework. Governance was migrated first (via issue #503) with the same store + warmup + `CachedGovernance` shape and is already merged. Bringing Governance in line is tracked as a priority follow-up issue that must complete before Phase 4 (#473) starts. Kept out of this PR to keep scope manageable.
- Phase 4+ feature work. This design only fixes the reference pattern.
- Removing `IMemoryCache` from the codebase generally. `IMemoryCache` remains available for genuinely cross-cutting ad-hoc signals (nav badge flags, notification meter). The rule is narrower: Application services and the canonical domain-data cache don't use it.

## Target pattern

Each §15-migrated section has three layers, with an optional fourth:

```
Controller / view component
  ↓ I<Section>Service                      (Application interface)
Caching<Section>Service                    [Infrastructure — optional decorator]
  ↓ I<Section>Service                      (+ IFull<Section>Invalidator)
<Section>Service                           [Application — pure logic]
  ↓ I<Section>Repository, cross-section service interfaces
<Section>Repository                        [Infrastructure — EF]
```

### Rules

1. **Application service is DB + pure logic.** Uses repositories, never `DbContext`. No `IMemoryCache`. No cache awareness. Removing the decorator leaves a fully functional service, just slower.
2. **Decorator owns the cache.** Private `ConcurrentDictionary<TKey, TFullDto>`. No `I*Store` interface, no warmup hosted service, no `IMemoryCache` for canonical domain data.
3. **One read method, `ValueTask`-returning.** `ValueTask<TFullDto?> GetFull<Section>Async(TKey, CancellationToken ct = default)`. Dict hit completes synchronously, zero allocation. Cold path wraps the inner call.
4. **Lazy warming, no hosted service.** Decorator fills on demand from individual reads. If a future bulk-read path emerges, it's the decorator's responsibility to handle full-set warming and flip an internal `_fullyWarm` flag.
5. **One-way invalidation signal.** `IFull<Section>Invalidator.InvalidateAsync(TKey, CancellationToken)` — implemented by the decorator. External sections inject it when user-state changes elsewhere make the cached view stale. The decorator reloads-or-removes the specific entry, preserving the `_fullyWarm` invariant. External code never mutates the cache directly.
6. **Sub-aggregates live in the parent's projection.** Entities that are conceptually part of a larger aggregate (e.g., a profile's CV entries) live inside the parent's Full-DTO projection and are written through the parent service. No separate service, repository, or decorator per sub-aggregate.

## Concrete changes to PR #235

### Renames

- `CachedProfile` → `FullProfile`. Moves to `Humans.Application/FullProfile.cs` (out of `IProfileService.cs`).
- `CachedVolunteerEntry` → `CVEntry`. Moves to `Humans.Application/CVEntry.cs`. Adds `LocalDate Date` field (was dropped from the old projection).
- `FullProfile.VolunteerHistory` → `FullProfile.CVEntries`.

### Deletions

- `Humans.Application/Interfaces/Stores/IProfileStore.cs`
- `Humans.Infrastructure/Stores/ProfileStore.cs`
- `Humans.Infrastructure/HostedServices/ProfileStoreWarmupHostedService.cs`
- Entire `Humans.Application/Interfaces/Stores/` directory (no other stores land here in this PR).
- `IVolunteerHistoryService` + `VolunteerHistoryService`
- `IVolunteerHistoryRepository` + `VolunteerHistoryRepository`
- `CachingVolunteerHistoryService`
- `tests/Humans.Application.Tests/Services/VolunteerHistoryServiceTests.cs` (and any other test files tied to the removed services)

### New interfaces

**`Humans.Application/Interfaces/IFullProfileInvalidator.cs`:**

```csharp
namespace Humans.Application.Interfaces;

public interface IFullProfileInvalidator
{
    Task InvalidateAsync(Guid userId, CancellationToken ct = default);
}
```

Production DI: `CachingProfileService` is the implementation for both `IProfileService` (as a Scrutor decorator) and `IFullProfileInvalidator`, resolved to the **same instance** so the dict and `_fullyWarm` flag are shared (not two separate copies). A `NullFullProfileInvalidator` no-op implementation is provided for test contexts that wire up `ProfileService` standalone without the decorator.

### Reshape `IProfileService`

**Remove:**

- `CachedProfile? GetCachedProfile(Guid userId);`
- `Task<CachedProfile?> GetCachedProfileAsync(Guid userId, CancellationToken ct = default);`
- `void UpdateProfileCache(Guid userId, CachedProfile? newValue);`

**Add:**

- `ValueTask<FullProfile?> GetFullProfileAsync(Guid userId, CancellationToken ct = default);`
- `Task SaveCVEntriesAsync(Guid userId, IReadOnlyList<CVEntry> entries, CancellationToken ct = default);` (absorbs the reconcile logic from `VolunteerHistoryService.ReconcileEntriesAsync`).

### Repository layer for CV entries

CV entries are a sub-aggregate of Profile. Rather than a separate `IVolunteerHistoryRepository`, their read/write methods move onto `IProfileRepository`:

- Reads: `IProfileRepository.GetByUserIdAsync` already `Include`s `VolunteerHistory`. No change.
- Writes: add `Task ReconcileCVEntriesAsync(Guid profileId, IReadOnlyList<CVEntry> entries, CancellationToken ct)` to `IProfileRepository`. Does the load-current / diff / add-remove-update / save cycle currently in `VolunteerHistoryService.ReconcileEntriesAsync`.

This keeps repository ownership aligned with aggregate ownership.

### Reshape `CachingProfileService`

- Holds private `ConcurrentDictionary<Guid, FullProfile> _byUserId` and `bool _fullyWarm`.
- Implements `IProfileService` (as decorator) and `IFullProfileInvalidator`.
- `GetFullProfileAsync`: dict hit returns `ValueTask.FromResult(hit)` synchronously; miss wraps the inner call, populates dict, returns.
- Write intercepts (`SaveProfileAsync`, `SetMembershipTierAsync`, `DeleteProfileAsync`, `SaveCVEntriesAsync`, etc.): delegate to inner, then call internal `RefreshEntryAsync(userId, ct)` which reloads Profile + User from repos, stitches a fresh `FullProfile`, and upserts (or removes if the profile no longer exists).
- `InvalidateAsync(userId, ct)`: same refresh logic as write intercepts. Reloads-or-removes. Preserves `_fullyWarm` (never leaves the dict with a stale or missing entry for an existing user).
- Nav badge and notification meter invalidation logic (currently done via `INavBadgeCacheInvalidator` / `INotificationMeterCacheInvalidator`) is unchanged — those remain as orthogonal cross-cutting signals, not canonical-data cache.

### Base `ProfileService`

- Gains `GetFullProfileAsync` implementation that loads Profile + User from repositories, stitches `FullProfile`, returns `ValueTask.FromResult(...)`. Always asynchronous under the hood, but the `ValueTask` shape keeps the decorator-path hot.
- Gains `SaveCVEntriesAsync` implementation (the reconcile-diff logic moved from `VolunteerHistoryService.ReconcileEntriesAsync`).
- No changes to other methods beyond the removals above.

### External-section callers

Replace each cross-section `_profileService.UpdateProfileCache(userId, ...)` call with `await _fullProfileInvalidator.InvalidateAsync(userId, ct)` and add `IFullProfileInvalidator` as a constructor dependency:

- `src/Humans.Infrastructure/Services/AccountMergeService.cs:155`
- `src/Humans.Infrastructure/Services/DuplicateAccountService.cs:298`
- `src/Humans.Infrastructure/Jobs/SuspendNonCompliantMembersJob.cs:182`
- `src/Humans.Infrastructure/Jobs/ProcessAccountDeletionsJob.cs:140`

The same-section `VolunteerHistoryService.cs:102` call disappears entirely — `VolunteerHistoryService` is deleted, CV writes go through `IProfileService.SaveCVEntriesAsync`, and the decorator's write intercept handles the refresh.

### Consumers of `GetCachedProfile` / `GetCachedProfileAsync`

Replace with `await _profileService.GetFullProfileAsync(userId)`:

- `src/Humans.Web/ViewComponents/UserAvatarViewComponent.cs:40-41` — collapses the `sync ?? await async` pattern into one `await`. The `ValueTask`-synchronous path means a warm-cache avatar render is still zero-allocation.
- `src/Humans.Web/TagHelpers/HumanLinkTagHelper.cs:78`
- `src/Humans.Web/ViewComponents/ProfileCardViewComponent.cs` — drops the `IVolunteerHistoryService` dependency and reads `CVEntries` off `FullProfile`.

### DI wiring

`src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs`:

- Remove: `AddSingleton<IProfileStore, ProfileStore>()`
- Remove: `AddHostedService<ProfileStoreWarmupHostedService>()`
- Remove: `AddScoped<IVolunteerHistoryService, VolunteerHistoryService>()` (and its `Decorate<>` for `CachingVolunteerHistoryService`).
- Remove: `AddScoped<IVolunteerHistoryRepository, VolunteerHistoryRepository>()`.
- Add: register `CachingProfileService` so one singleton instance serves both `IProfileService` (via Scrutor `Decorate<>`) and `IFullProfileInvalidator`. Exact registration idiom is an implementation detail for the plan; the invariant is single-instance so state is shared. `NullFullProfileInvalidator` is registered in test-host composition only, not in the production DI wiring.

## Data flow

### Read (FullProfile)

```
Caller
  ↓ await _profileService.GetFullProfileAsync(userId)
CachingProfileService
  ├─ dict.TryGetValue(userId) → hit → ValueTask.FromResult(fullProfile)
  └─ miss → await _inner.GetFullProfileAsync(userId)
              ↓
            ProfileService
              var profile = await _repo.GetByUserIdAsync(userId, ct);  // includes VolunteerHistory
              var user    = await _userService.GetByIdAsync(userId, ct);
              return profile is null ? null : FullProfile.Create(profile, user);
              ↑
           CachingProfileService (cont.)
              if (result is not null) dict[userId] = result;
              return result;
```

### Write (any Profile mutation, including CV entries)

```
Caller
  ↓ await _profileService.SaveProfileAsync(...)    (or SaveCVEntriesAsync, etc.)
CachingProfileService
  await _inner.SaveProfileAsync(...);
  await RefreshEntryAsync(userId, ct);   // reload + stitch + upsert (or remove if gone)
```

### Cross-section invalidation

```
AccountMergeService.MergeAsync(source, target)
  ...merge logic...
  await _fullProfileInvalidator.InvalidateAsync(sourceUserId, ct);
  // Decorator.InvalidateAsync:
  //   reload Profile + User for sourceUserId
  //   profile is now null → dict.TryRemove(sourceUserId)
  //   _fullyWarm invariant preserved: dict still has an entry for every existing profile.
```

## Testing

### Architecture tests (`tests/Humans.Application.Tests/Architecture/ProfileArchitectureTests.cs`)

- Remove any `IProfileStore` / warmup hosted service assertions.
- Keep: `ProfileService` lives in `Humans.Application.Services.Profile`.
- Keep: `ProfileService` constructor takes no `DbContext`, no `IMemoryCache`.
- Keep: `ProfileService` constructor takes `IProfileRepository`.
- Add: `ProfileService` constructor takes no type from `Humans.Application.Interfaces.Stores.*`.
- Add: `CachingProfileService` implements both `IProfileService` and `IFullProfileInvalidator`.

### Application unit tests

- Remove `GetCachedProfile` / `GetCachedProfileAsync` / `UpdateProfileCache` tests from `ProfileServiceTests`.
- Absorb reconcile-logic tests from `VolunteerHistoryServiceTests` into `ProfileServiceTests` under the new `SaveCVEntriesAsync` method.
- Add `CachingProfileServiceTests` (new file) covering:
  - Dict hit returns without touching `_inner`.
  - Dict miss delegates to `_inner`, populates dict, returns.
  - `SaveProfileAsync` refreshes the affected entry in the dict.
  - `InvalidateAsync` on an existing user reloads the entry.
  - `InvalidateAsync` on a deleted user removes the entry.
  - `_fullyWarm` flag survives invalidations (no full cache clear).

### Verification gate

- `dotnet build Humans.slnx` — 0 errors, 0 warnings.
- `dotnet test Humans.slnx` — all tests pass (Domain + Application + Web).
- `dotnet format --verify-no-changes` — clean.
- Browser smoke test on the preview environment: profile view/edit, contact fields, email management, CV-entry edit via Profile/Edit, admin human detail, profile search, avatar rendering on team pages (warm-cache path).

## §15 design-rules doc rewrite

Replace the current "repo + store + decorator + warmup hosted service" section of `docs/architecture/design-rules.md` with the Target pattern and Rules sections above, verbatim. Update the flow diagram (currently shows `ProfileRepository, ProfileStore` at the bottom) to the shape in this spec. Update the `CampService` example constructor signature in §15 to drop `IProfileStore`.

## PR #235 body rewrite

After the cache-collapse commits land on `sprint/2026-04-16/issue-504`, amend the PR description:

- Replace the "Store layer" paragraph with: "Caching decorator owns a singleton `ConcurrentDictionary<Guid, FullProfile>`. Lazy-warmed on demand. No warmup hosted service."
- Replace the "Decorator layer" paragraph with: "CachingProfileService implements both IProfileService (decorator) and IFullProfileInvalidator. One cache, one invalidation path."
- Add an "Architectural correction" note linking to this design doc.
- Keep the "Closes #504" and the test plan.

## Follow-up work (out of scope for this PR)

A priority GitHub issue against `nobodies-collective/Humans` will be filed to bring the Governance section in line with the corrected pattern — rename `CachedGovernance` → `FullGovernance`, collapse `IGovernanceStore` + warmup into decorator-owned dict, align architecture tests. The issue is blocking for §15 Phase 4 (#473). Tracked separately from this PR to keep #235 reviewable in isolation.

## Commit strategy

Amend PR #235 with additional commits on the existing branch. No rebase-squash, no new PR. Commit history during review shows the rework path (original migration → review fixes → cache collapse + volunteer-history fold). When the PR merges to `upstream/main`, it squash-merges to a single clean commit.

## Implementation outcome (added 2026-04-20, post-Phase 11)

This spec was the starting blueprint. The final implementation (Phases 1–11 of PR #235) arrived at the same destination on all major design decisions, with a few deviations from the exact DI wiring proposed here:

- **No Scrutor `Decorate<>`** — the DI wiring registers `CachingProfileService` as a named Singleton and forwards `IProfileService` via a factory lambda, rather than using Scrutor's decorator helper. The keyed-inner pattern (`AddKeyedScoped` + `AddSingleton<IProfileService>(sp => sp.GetRequiredService<CachingProfileService>())`) was used instead, because `Decorate<>` does not interact cleanly with keyed registrations and the `IUserDataContributor` forwarding.
- **`ProfileService` inner is Scoped, not Singleton.** The spec assumed the inner service could be a Singleton; in practice it has many Scoped cross-section dependencies (`UserManager`, `IRoleAssignmentService`, etc.) that cannot be made Singleton without a cascading refactor. The per-call `IServiceScopeFactory.CreateAsyncScope()` pattern was adopted instead.
- **`_fullyWarm` flag not yet activated.** The flag was declared as reserved for future bulk-read triggers (§15g) and is not currently in the code.
- **`NullFullProfileInvalidator`** exists at `src/Humans.Infrastructure/Services/Profiles/NullFullProfileInvalidator.cs` but is not registered in production DI; test contexts that need a no-op invalidator mock the interface directly.
- **Cross-section ShiftAuthorization staleness (§15 NEW-B)** is a known regression documented in the code and in `design-rules.md §15g`.

**The canonical, code-verified pattern is `docs/architecture/design-rules.md §15`.** That section is the authoritative reference for all future §15 section migrations. This document is a historical artifact — it reflects what was proposed before implementation, not what landed.

Governance alignment (old-pattern `IApplicationStore` + warmup): tracked on `nobodies-collective/Humans` issue #533. Must complete before §15 Phase 4 (Google integration, issue #473) starts.
