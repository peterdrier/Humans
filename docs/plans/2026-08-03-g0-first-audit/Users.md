# Users — G0 First Audit

**Section:** Users · **Kind:** shared contract (Foundational, per `CONTEXT.md`) · **Audited:** 2026-08-03 @ 5a9bbe198

> Scored jointly with [Profiles.md](Profiles.md) per `memory/architecture/users-profiles-one-section.md` — Users, Profiles, and UserEmail are **one ownership section ("Humans")**. This file covers the User/EventParticipation/AccountMerge sub-aggregates and the Identity framework surface; see Profiles.md for the Profile/ContactField/UserEmail/CommunicationPreference sub-aggregates. Mechanical results (ownership-violations, baseline scan) are shared with Profiles.md since `reforge` and the baseline files score the combined "Humans" section as one unit.

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository in-section | PASS | Same combined-section check as Profiles.md: `reforge ownership-violations --owner Humans --tables …` → **0 violations**, covering `users`, `event_participations`, `account_merge_requests` plus the Identity tables (Identity-framework-managed, not custom-repo, per §2a exception). |
| 2 | One writer-service per table | **FAIL** | Same `UserInfoSaveChangesInterceptor` finding as Profiles.md — it exists specifically to catch Identity-machinery bypasses of `IUserService` on `users`. Out-of-scope-tonight debt per orchestrator briefing. |
| 3 | No EF entity leaks across boundary | **FAIL** | Same 2 baseline rows as Profiles.md (`IUserService.GetByIdsAsync`, `IAccountProvisioningService.FindOrCreateUserByEmailAsync` both return `User`) — these interfaces live under `Interfaces.Users`, so they're scored here as the owning namespace. |
| 4 | No cross-section EF joins | PASS | No `CrossSectionEfJoinAnalyzer` baseline entries found for this section. |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / owned baseline rows | **FAIL** | The 2 entity-leak baseline rows above are owned here. On the positive side: the `[Obsolete]`-marked shadow columns (`User.GoogleEmail`, `User.NormalizedEmail`, `UserEmail.IsOAuth`/`DisplayOrder`) are properly documented, intentional, soak-pending drops (not leaks) — `UserEmailLegacyFieldRestrictionsTests` enforces no live reader/writer. Six User-side cross-domain navs were already stripped in issue #635; only `User.UserEmails` and `User.EventParticipations` remain declared, both legitimately section-owned. |
| 6 | Controllers thin (no HUM0031 grandfathers) | **FAIL (tracked)** | `AccountController.cs` (1 grandfather, "107 statements, cc 33") and `UsersAdminDebugController.cs` (1 grandfather, sort-key switch) both carry `[Grandfathered(ruleId: "HUM0031", …, issueRef: "nobodies-collective/Humans#857")]`. Same in-flight burndown lane as Profiles. |
| 7 | `docs/sections/Users.md` current | PASS (high confidence) | Recent, detailed — references #703 caching decorator (`UserInfo` dict), #635 nav strip, #701 `LoggingUserStoreDecorator` retirement, #685 deletion-cascade DI-cycle fix. No drift observed. |

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests on real Postgres, zero EF-InMemory | **FAIL** | `UserRepositoryTests.cs:23` and `UserRepositoryUserEmailsTests.cs:24` both use `.UseInMemoryDatabase(...)`. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | **FAIL — corrected 2026-08-03** | The first group is genuinely clean (`AccountMergeServiceMergeTests.cs`, `DuplicateAccountServiceTests.cs`, `AccountDeletionServiceTests.cs`, `AccountProvisioningServiceTests.cs`, `UserEmailServiceTests.cs` — zero `ServiceTestHarness`/`HumansDbContext` hits, properly mocked). But one of the files this row named as *unverified* is a violation: `UserServiceProfileOnboardingMutationTests` extends `ServiceTestHarness`, constructs a concrete `UserRepository(DbFactory, Clock)`, and seeds/reads the harness's EF-InMemory `Db` directly (`ServiceTestHarness.cs:54,71`). Add it to the #766 conversion queue. |
| 3 | Invariants/triggers each have a test | PARTIAL | Spot-check: `Attended`-monotonic invariant (ticket-sync cannot downgrade `Attended`) has at least one reference in `CachingUserServiceTests.cs`. Full traceability against the doc's ~9 invariants / 10+ triggers not verified. |
| 4 | No skipped tests without issue ref | PASS (tentative) | No `Skip="..."` hits found in this section's test files. |
| 5 | Tests grouped under section | PARTIAL | `tests/Humans.Application.Tests/Services/Users/` exists and is used, but `AccountMergeServiceMergeTests.cs`, `DuplicateAccountServiceTests.cs`, `AccountProvisioningServiceTests.cs`, `UnsubscribeServiceTests.cs` sit flat at `Services/` root rather than inside `Services/Users/`. Same repo-wide flat-naming pattern noted in Profiles.md. |

## G1 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| `UserInfoSaveChangesInterceptor` interceptor workaround | Infrastructure interceptor registration | Same item as Profiles.md — out of scope tonight, track as follow-up. | y |
| `IUserService.GetByIdsAsync` / `IAccountProvisioningService.FindOrCreateUserByEmailAsync` return `User` entity | `Interfaces.Users` | Same item as Profiles.md — project to `UserInfo`, remove baseline rows. This is the highest-traffic entity leak in the codebase (both methods are called from nearly every other section per the Cross-Section Dependencies tables across all 9 audited sections). | y |
| `AccountController`/`UsersAdminDebugController` HUM0031 grandfathers | `src/Humans.Web/Controllers/` | Tracked under #857 (Lane 2 tonight). | y |

## G2 queue notes

`User.GoogleEmailStatus`, `User.GoogleEmail` (shadow), `UserEmail.IsOAuth`/`DisplayOrder` (shadow), `Profile.IsSuspended` legacy bool (cross-referenced in Profiles.md) are the section's demolition-inventory items, already named in the doc as soak-pending drops. No new items surfaced.
