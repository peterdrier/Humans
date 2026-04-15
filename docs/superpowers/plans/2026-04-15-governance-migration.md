# Governance Migration — Implementation Plan

> First full end-to-end implementation of the target repository/store/decorator pattern per `docs/architecture/design-rules.md`. Reference template for every subsequent section migration. Issue #503.

**Goal:** Migrate `ApplicationDecisionService` + 3 owned tables (`applications`, `application_state_histories`, `board_votes`) from "service-in-Infrastructure with direct `DbContext`" to "repository + store + decorator" — services in Application, repo/store in Infrastructure, cross-cutting caching via Scrutor decorator.

**Base:** Branch `governance-migration` off `origin/main` (not `upstream/main` — upstream is behind on NuGet package fixes and its baseline build fails; since the PR target is `peterdrier/Humans:main`, origin/main yields both a buildable baseline AND a clean diff for review).

---

## Architecture

- **Repository** `IApplicationRepository` in `Humans.Application/Interfaces/Repositories/`, impl in `Humans.Infrastructure/Repositories/`. The only non-test file that touches `DbContext.Applications`/`BoardVotes`/`ApplicationStateHistories` after this PR (OnboardingService/ProfileService/jobs still read `DbContext.Applications` — tracked as known violations for their own future migrations, per §15c).
- **Store** `IApplicationStore` dict-backed (`ConcurrentDictionary<Guid, Application>`), singleton, warmed at startup via `IHostedService` calling `IApplicationRepository.GetAllAsync()`.
- **Service** `ApplicationDecisionService` moves from `Humans.Infrastructure/Services/` to `Humans.Application/Services/`. No `HumansDbContext`, no `IMemoryCache`. Constructor takes repo + store + `IUserService` + `IProfileService` + `IAuditLogService` + `IEmailService` + `INotificationService` + `ISystemTeamSync` + `IHumansMetrics` + `IClock` + `ILogger`.
- **Decorator** `CachingApplicationDecisionService` in `Humans.Infrastructure/Services/`, registered via Scrutor `.Decorate<IApplicationDecisionService, CachingApplicationDecisionService>()`. Read paths (`GetUserApplicationsAsync`) can serve from the store. Write paths pass through to the inner service, which updates the store after repo write; decorator handles cross-cutting cache invalidation.
- **Cross-domain navs stripped** from `Application`, `ApplicationStateHistory`, `BoardVote`. EF configurations use `HasOne<User>().WithMany().HasForeignKey(...)` (no nav property).
- **In-memory stitching** replaces 8 `.Include` chains. Three read methods change signature to return DTOs instead of entities: `GetApplicationDetailAsync → ApplicationAdminDetailDto?`, `GetUserApplicationDetailAsync → ApplicationUserDetailDto?`, `GetFilteredApplicationsAsync → (IReadOnlyList<ApplicationAdminRowDto>, int)`. Write methods keep `ApplicationDecisionResult`. `GetUserApplicationsAsync` keeps `IReadOnlyList<Application>` (no cross-domain access on result).
- **New interfaces added** to `IUserService` (`GetByIdAsync`, `GetByIdsAsync`) and `IProfileService` (`GetByUserIdsAsync`, `SetMembershipTierAsync`). Minimal impl additions to the existing services in Infrastructure.
- **11 inline cache invalidations** moved out of the service:
  - `NavBadgeCounts` × 4 → `INavBadgeCacheInvalidator` interface, impl wraps existing `_cache.InvalidateNavBadgeCounts()`
  - `NotificationMeters` × 4 → `INotificationMeterCacheInvalidator`
  - `UserProfile` × 1 → invoked as side-effect of `IProfileService.SetMembershipTierAsync` (ProfileService decorator owns this once Profile migrates)
  - `VotingBadge` × 2 → `IVotingBadgeCacheInvalidator`
  Decorator handles NavBadge/NotificationMeter/VotingBadge. Profile cache flows through ProfileService.

## Scope creep — required to make compile after nav strip

- **OnboardingService** `GetBoardVotingDashboardAsync` + `GetBoardVotingDetailAsync` — currently use `.Include(a => a.User).ThenInclude(u => u.Profile)` and `.Include(bv => bv.BoardMemberUser)`. Rewrite to fetch applications, then fetch users + profiles + voter users by ids in parallel, stitch into new DTOs.
- **TermRenewalReminderJob** — `.Include(a => a.User).ThenInclude(u => u.UserEmails)` rewritten as two queries + in-memory stitch.
- **SendBoardDailyDigestJob** — `.Include(a => a.User)` at line 81 rewritten the same way.
- **ApplicationController** — switch to new DTO shapes from decision service.
- **OnboardingReviewController** — switch to new DTO shapes from OnboardingService BoardVoting methods.

## Out of scope

Other sections do NOT migrate. ProfileService/UserService only get new method additions. No other controller, job, or service is restructured. Views are not touched — only how ViewModels are populated in controllers changes.

---

## Autonomous decisions

1. **Base branch is `origin/main`, not `upstream/main`.** Upstream lags on NuGet package versions and its build fails. PR target is `peterdrier/Humans:main`. Documented in PR body.
2. **Cross-service atomicity is deliberately limited.** `IApplicationRepository.FinalizeAsync(app, ct)` commits `applications` update + `board_votes` bulk-delete in one `SaveChangesAsync`. Profile tier update (`IProfileService.SetMembershipTierAsync`) and audit log write (`IAuditLogService.LogAsync`) run in separate transactions. Partial failure (finalized application with stale profile tier) is possible but rare and recoverable at ~500-user single-server scale. Documented in PR body under "Transaction boundary notes".
3. **Three cache-invalidation interfaces** (NavBadge, NotificationMeter, VotingBadge) instead of inlining the `IMemoryCache` calls in the decorator. Makes the cross-section coupling visible and testable. Impls live in `Humans.Infrastructure/Caching/MemoryCacheInvalidators.cs`.
4. **Warmup via `IHostedService.StartAsync`** (synchronous, runs before host accepts requests). Fails the host if repo read fails — intentional; a warmup failure indicates DB connectivity problems that would break every request anyway.
5. **Architecture test asserts three reflection-based properties:**
   - `typeof(ApplicationDecisionService).Namespace == "Humans.Application.Services"`
   - constructor has no `DbContext`-typed parameter
   - `Humans.Application.dll` has zero references to any `Microsoft.EntityFrameworkCore*` assembly
   The "only repository touches DbContext.Applications" goal is deferred — other sections still legitimately read the table until they migrate.
6. **`IProfileService.SetMembershipTierAsync` is added as a new method on IProfileService.** The Infrastructure-resident ProfileService impl updates Profile.MembershipTier + UpdatedAt, invalidates `CacheKeys.UserProfile(userId)`, and updates the profile cache. One-liner additions — no rewrite.
7. **Test file for ApplicationDecisionService moves to mocked repo/store.** Existing in-memory EF + NSubstitute pattern preserved. `ApplicationRepository` is backed by in-memory EF; `ApplicationStore` is used directly; `IUserService`/`IProfileService` are mocked via NSubstitute.
8. **Existing ApplicationDecisionServiceTests that asserted `.User` nav** are rewritten to assert stitched DTO fields. Net test count stays close to the baseline (±2).

---

## File Map

**New — Application layer**
- `src/Humans.Application/DTOs/Governance/ApplicationAdminRowDto.cs`
- `src/Humans.Application/DTOs/Governance/ApplicationAdminDetailDto.cs`
- `src/Humans.Application/DTOs/Governance/ApplicationUserDetailDto.cs`
- `src/Humans.Application/DTOs/Governance/ApplicationStateHistoryDto.cs`
- `src/Humans.Application/DTOs/Governance/BoardVotingDashboardRow.cs` (OnboardingService-facing)
- `src/Humans.Application/DTOs/Governance/BoardVotingDetailData.cs`
- `src/Humans.Application/DTOs/Governance/BoardVoteRow.cs`
- `src/Humans.Application/Interfaces/Repositories/IApplicationRepository.cs`
- `src/Humans.Application/Interfaces/Stores/IApplicationStore.cs`
- `src/Humans.Application/Interfaces/Caching/INavBadgeCacheInvalidator.cs`
- `src/Humans.Application/Interfaces/Caching/INotificationMeterCacheInvalidator.cs`
- `src/Humans.Application/Interfaces/Caching/IVotingBadgeCacheInvalidator.cs`
- `src/Humans.Application/Services/ApplicationDecisionService.cs` (moved from Infrastructure)

**New — Infrastructure layer**
- `src/Humans.Infrastructure/Repositories/ApplicationRepository.cs`
- `src/Humans.Infrastructure/Stores/ApplicationStore.cs`
- `src/Humans.Infrastructure/Services/CachingApplicationDecisionService.cs`
- `src/Humans.Infrastructure/HostedServices/ApplicationStoreWarmupHostedService.cs`
- `src/Humans.Infrastructure/Caching/MemoryCacheInvalidators.cs`

**Modified — Application layer**
- `src/Humans.Application/Interfaces/IApplicationDecisionService.cs` — 3 signatures change
- `src/Humans.Application/Interfaces/IUserService.cs` — add `GetByIdAsync`, `GetByIdsAsync`
- `src/Humans.Application/Interfaces/IProfileService.cs` — add `GetByUserIdsAsync`, `SetMembershipTierAsync`

**Modified — Domain layer**
- `src/Humans.Domain/Entities/Application.cs` — strip `User`, `ReviewedByUser`
- `src/Humans.Domain/Entities/ApplicationStateHistory.cs` — strip `ChangedByUser`
- `src/Humans.Domain/Entities/BoardVote.cs` — strip `BoardMemberUser`

**Modified — Infrastructure layer**
- `src/Humans.Infrastructure/Data/Configurations/ApplicationConfiguration.cs` — `HasOne<User>()` without nav
- `src/Humans.Infrastructure/Data/Configurations/ApplicationStateHistoryConfiguration.cs` — same
- `src/Humans.Infrastructure/Data/Configurations/BoardVoteConfiguration.cs` — same
- `src/Humans.Infrastructure/Services/UserService.cs` — add two new read methods
- `src/Humans.Infrastructure/Services/ProfileService.cs` — add two new methods
- `src/Humans.Infrastructure/Services/OnboardingService.cs` — rewrite `GetBoardVotingDashboardAsync` + `GetBoardVotingDetailAsync`
- `src/Humans.Infrastructure/Jobs/TermRenewalReminderJob.cs` — remove `.Include(a => a.User)`
- `src/Humans.Infrastructure/Jobs/SendBoardDailyDigestJob.cs` — remove `.Include(a => a.User)`
- `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs` — wire up repo/store/decorator/invalidators/warmup
- `Directory.Packages.props` — add `Scrutor`
- `src/Humans.Infrastructure/Humans.Infrastructure.csproj` — `<PackageReference Include="Scrutor" />`

**Modified — Web layer**
- `src/Humans.Web/Controllers/ApplicationController.cs` — consume new DTOs
- `src/Humans.Web/Controllers/OnboardingReviewController.cs` — consume new DTOs
- `src/Humans.Web/Models/` — BoardVoting view models may need DTO-shape alignment

**Tests**
- `tests/Humans.Application.Tests/Repositories/ApplicationRepositoryTests.cs` (new)
- `tests/Humans.Application.Tests/Stores/ApplicationStoreTests.cs` (new)
- `tests/Humans.Application.Tests/Services/CachingApplicationDecisionServiceTests.cs` (new)
- `tests/Humans.Application.Tests/Architecture/GovernanceArchitectureTests.cs` (new)
- `tests/Humans.Application.Tests/Services/ApplicationDecisionServiceTests.cs` (rewrite)

**Docs**
- `docs/sections/Governance.md` — delete Target Architecture Direction block
- `docs/architecture/design-rules.md` §15c — update violation counts
- `docs/architecture/maintenance-log.md` — log completion

---

## Task Order

Single commit per logical task group. Each task's build must be clean before advancing (exception: Task 6/7 strips navs and fixes cascades as one atomic unit — the build is broken between Task 6 and Task 7, so they commit together).

1. **Scrutor package** — `Directory.Packages.props` + `Humans.Infrastructure.csproj`. Baseline build + baseline test count recorded. Commit: `chore: add Scrutor package for decorator DI`.
2. **Governance DTOs** — all 7 DTOs in `Humans.Application/DTOs/Governance/`. Commit: `feat: add governance DTOs for repo/store/decorator migration`.
3. **Cache-invalidator interfaces + impls** — `I{NavBadge,NotificationMeter,VotingBadge}CacheInvalidator` in Application, `MemoryCacheInvalidators.cs` in Infrastructure. Commit: `feat: add cross-cutting cache-invalidator interfaces`.
4. **IApplicationRepository + impl + tests** — interface, impl, 6+ repo tests. Commit: `feat(governance): add IApplicationRepository + impl`.
5. **IApplicationStore + impl + tests + warmup hosted service** — interface, impl, 7+ store tests, hosted service. Commit: `feat(governance): add IApplicationStore + warmup hosted service`.
6. **Extend IUserService + IProfileService** — new method signatures + impls. Commit: `feat: add IUserService.GetByIdsAsync and IProfileService.SetMembershipTierAsync`.
7. **The big one — atomic cascade:**
   - Strip cross-domain navs from 3 entities
   - Update 3 EF configurations
   - Create new `Humans.Application/Services/ApplicationDecisionService.cs` with new DTO-returning signatures + in-memory stitching
   - Delete old `Humans.Infrastructure/Services/ApplicationDecisionService.cs`
   - Rewrite `OnboardingService.GetBoardVotingDashboardAsync` + `GetBoardVotingDetailAsync` to stitch DTOs instead of navs
   - Rewrite `TermRenewalReminderJob` to stitch users separately
   - Rewrite `SendBoardDailyDigestJob` to stitch users separately
   - Update `ApplicationController` for new DTO consumption
   - Update `OnboardingReviewController` for new DTO consumption
   - Rewrite `ApplicationDecisionServiceTests` to use repo/store + mocked IUserService/IProfileService
   - Interface signature changes in `IApplicationDecisionService`
   - Commit: `refactor(governance): strip cross-domain navs + move service to Application`.
8. **CachingApplicationDecisionService decorator** — new file, DI wiring via Scrutor + invalidator registrations + hosted service registration. Commit: `feat(governance): add caching decorator + DI wiring`.
9. **Decorator tests** — `CachingApplicationDecisionServiceTests.cs`. Commit: `test(governance): add decorator coverage`.
10. **Architecture test** — reflection-based assertions. Commit: `test(governance): add architecture tests enforcing service placement`.
11. **Docs** — delete Governance.md block, update design-rules.md §15c, log maintenance. Commit: `docs(governance): mark migration complete`.
12. **Full verification + smoke test** — build, test, EF dry-run migration check, browser walkthrough (submit → vote × 2 → approve → verify tier + team + DB). No separate commit.
13. **Code review (skill)** + fixes + push + PR against peterdrier/Humans:main. PR description includes: summary, #503 link, cache invalidation decisions, transaction boundary notes, reference-for-future-migrations pointer, autonomous decisions list, test plan checklist.

---

## Non-obvious technical notes

- **`FinalizeAsync(app, ct)`** takes the already-mutated application (by `app.Approve()` / `app.Reject()`), calls `_dbContext.Applications.Update(application)`, then `_dbContext.BoardVotes.Where(bv => bv.ApplicationId == application.Id).ExecuteDeleteAsync(ct)`, then `_dbContext.SaveChangesAsync(ct)`. Single transaction. Voter ids for cache invalidation must be fetched by the decorator BEFORE calling `FinalizeAsync` via `_repository.GetVoterIdsForApplicationAsync(appId, ct)`.
- **`ApplicationStore` is singleton.** `ApplicationRepository` is scoped (wraps scoped DbContext). Warmup hosted service creates its own scope to resolve the repo.
- **`IUserDataContributor` interface** for GDPR export is still implemented by the new `ApplicationDecisionService`. DI forwarding registration preserves the "single scoped instance → two interface projections" pattern.
- **No EF migration needed.** Stripping a nav property doesn't change the FK column. `dotnet ef migrations add` should produce an empty migration — if not, something's wrong.
- **NodaTime throughout.** `Instant`, `LocalDate`. Never `DateTime`/`DateTimeOffset`.
- **Logging rule:** always log errors even when expected (drop exception/stack optionally, but never delete the log call).
- **No concurrency tokens** anywhere.
- **The migration preserves behavior exactly** — same workflows, same side effects (email, notification, team sync), same audit log. The only behavioral change is the cross-service transaction gap on Approve: profile tier update is no longer in the same transaction as the application finalize. This is accepted and documented.
