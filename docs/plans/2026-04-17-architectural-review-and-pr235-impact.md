# Architectural Review + PR #235 Impact Analysis

**Date:** 2026-04-17
**Author:** Claude (holistic review session)
**Context:** Review requested before adding 5–15 new modules over the next month. Revised after examining PR #235.

---

## Part 1 — Initial architectural snapshot

### Scale today

- **323K LOC total.** 65 services, 19 controllers, 873 tests, 143 migrations, 17 jobs.
- **Growth:** ~20K LOC/month, 2.4 migrations/day, 360 commits in the last 30 days.
- **Active work:** Phase 3 of the service-ownership refactor in flight; multiple parallel migration PRs.

### Layer health

| Layer | Files | LOC | Notes |
|---|---|---|---|
| Domain | 137 | 5,349 | 61 entities, 56 enums, not anemic |
| Application | 111 | 6,065 | Interfaces + DTOs + (now) services |
| Infrastructure | 308 | 265,217 | Includes 143 migrations and 65 service impls |
| Web | 178 | 26,370 | 19 controllers, 207 views |
| Tests | 89 | 21,518 | 873 test methods |

### Service layer signal

- 65 service implementations; top 5 by size: `GoogleWorkspaceSyncService` (2,500 LOC), `TeamService` (2,379), `CampService` (1,290), `ProfileService` (1,219), `BudgetService` (1,162).
- 14 services inject 8+ dependencies (`HumansMetricsService` 12, `OnboardingService` 11, `TeamService` 10, `ProfileService` 9). Most are legitimate orchestrators.
- CLAUDE.md rule: never split god services — caching ownership would break.

### Web layer signal

| Controller | LOC | Actions | Deps |
|---|---|---|---|
| `ProfileController` | 1,671 | 37 | 20 (including `HumansDbContext` — violates DESIGN_RULES §2a) |
| `TeamAdminController` | 1,068 | 24 | 10 |
| `TeamController` | 925 | 17 | 12 |
| `CampController` | 876 | 19 | 6 |
| `GoogleController` | 846 | 32 | 7 |

Views: `ShiftAdmin/Index.cshtml` 1,317 LOC, `Profile/Edit.cshtml` 1,061 LOC. Minimal view-component reuse.

### Testing signal

- **Application tests: 3.29x ratio** (19,952 LOC tests / 6,065 LOC source) — excellent.
- **Integration tests: 0.001x ratio** (389 LOC / 265,217 LOC Infrastructure) — effectively zero DB/EF integration coverage.
- 821 `[Fact]` + 52 `[Theory]` across the suite.

### Other signals

- **Authorization:** 7 resource-based handlers, 39 `[Authorize]` attributes across 19 controllers and 200+ actions — per-action coverage is incomplete.
- **Caching:** 16 services use `IMemoryCache` with ad-hoc key generation; no shared `ICacheKeyFactory`.
- **Background jobs:** 17 Hangfire jobs, top-heavy (`SystemTeamSyncJob` 698 LOC, `ProcessAccountDeletionsJob` 278 LOC).
- **Documentation:** 37 feature specs + 14 section invariants; doc-to-code ratio 0.185 (below industry standard).
- **Tech debt markers:** 1 TODO across the codebase. Debt lives in GitHub issues, not comments.

---

## Part 2 — Original top 5 recommendations

Before examining PR #235, the recommendations ranked by compounding impact:

### 1. Integration-test foundation before the module surge

**Gap:** 389 LOC of integration tests against 265K LOC of Infrastructure. Application unit tests are strong but nothing exercises real Postgres, real migration apply, real EF change-tracker, or real service-to-service interaction with caching in the loop.

**What to build:**
- Testcontainers-based Postgres 17 fixture (matches prod).
- A mandatory "smoke" integration test per module: happy path + one cross-service call + cache invalidation round-trip + clean migration apply on seeded DB.
- Fixture data for load-bearing entities (User/Team/Profile) so migration tests run against realistic row counts.
- Wire into `execute-sprint` and `pr-review` skills as blocking checks.

### 2. Structural guardrails on the Web layer

**Evidence:** `ProfileController` — 1,671 LOC, 37 actions, 20 deps, directly injects `HumansDbContext` (hard violation of DESIGN_RULES §2a). `TeamAdminController` / `TeamController` / `CampController` / `GoogleController` in the same weight class.

**What to do:**
- Split `ProfileController` → `ProfileController` (self-service) + `ProfileAdminController`. Remove DbContext injection.
- Introduce Razor View Components for recurring UI blocks (profile-picture, contact-fields-table, volunteer-history-table).
- Analyzer or CI check: controller >800 LOC, >20 actions, or injecting `HumansDbContext` fails the build. Apply to new code only.

### 3. Module scaffold for new modules

**Observation:** No template exists. Each new module reinvents where services go, how cache keys are shaped, whether an authorization handler is needed, whether a section invariant doc is required.

**What to build:** a `new-module` skill or dotnet template that scaffolds Domain entity, Application interfaces + DTOs, Infrastructure service stub, migration placeholder, controller stub with `[Authorize]`, views with View Component slots, `docs/features/<name>.md`, `docs/sections/<name>.md`, test skeletons, and pre-wired DI registration.

### 4. Authorization coverage + compile-time enforcement

**Gap:** 39 `[Authorize]` attributes across 19 controllers exposing hundreds of actions.

**What to build:** a Roslyn analyzer or test that inspects every non-abstract controller action and fails unless it has `[Authorize]` or an explicit `[AllowAnonymous]`. Audit existing actions once against this rule.

### 5. Unified module-launch gate

**Observation:** Skills like `spec-check`, `ef-migration-reviewer`, `code-review`, `pr-review`, `check-pr`, `triage`, `nav-audit` exist but are invoked individually and inconsistently.

**What to build:** a `launch-module` skill that runs in order: spec-check → ef-migration-reviewer (if migrations) → integration test exists → authorization clean → `docs/features/<name>.md` exists → `docs/sections/<name>.md` updated → nav link exists → code-review → pr-review.

---

## Part 3 — What PR #235 actually is

Peter pointed at PR #235, which substantially changes the calculus.

### The PR

- **Title:** "Migrate Profile section to repo/store/decorator (§15 Step 0)"
- **Branch:** `sprint/2026-04-16/issue-504` → `main`
- **Diff:** +4,278 / −1,498 across 72 files
- **State:** OPEN, mergeable, 1,010 of 1,011 tests passing (1 pre-existing DataProtection failure)
- **Closes:** `nobodies-collective/Humans#504`

### The pattern it establishes

| Layer | Location | Role |
|---|---|---|
| **Repository** | `Humans.Application/Interfaces/Repositories/` + `Humans.Infrastructure/Repositories/` | EF-only data access, scoped. Takes DbContext, returns entities. No logic, no side effects. |
| **Store** | `Humans.Application/Interfaces/Stores/` + `Humans.Infrastructure/Stores/` | Singleton `ConcurrentDictionary`. Whole-collection in-memory cache warmed at startup by a `HostedService`. Keyed per user. |
| **Application service** | `Humans.Application/Services/<Section>/` | Business logic. Injects repositories (own tables) + service interfaces (other sections). **Forbidden from touching `DbContext` or `IMemoryCache`.** |
| **Caching decorator** | `Humans.Infrastructure/Services/<Section>/` | Wraps the service via Scrutor `Decorate<>()`. Handles per-request IMemoryCache, store refresh after writes, cross-cutting invalidation (nav badge, notification meter). |
| **Architecture test** | `tests/Humans.Application.Tests/Architecture/` | Reflection-based xUnit tests that **fail the build** if a service takes `DbContext`, takes `IMemoryCache`, lives in the wrong namespace, or isn't decorated. |

### Canonical flow (Profile example)

```
Controller
  ↓ IProfileService
CachingProfileService (decorator, Infrastructure)
  ↓ IProfileService
ProfileService (Application — pure, no EF, no cache)
  ↓ IProfileRepository, IProfileStore
ProfileRepository (Infrastructure — EF) + ProfileStore (Infrastructure — singleton dict)
```

**Write path:** controller → decorator → service → repo → DB, then decorator invalidates nav badge + notification meter + per-user caches, then refreshes the store by stitching profile + user + notification email into `CachedProfile`.

**Read path:** controller → decorator (checks per-user 2-min IMemoryCache) → service → repo (or reads directly from the singleton store for hot paths).

**Cross-section reads:** `ProfileService` injects `IApplicationDecisionService`, `ICampaignService`, `ITeamService`, `IRoleAssignmentService` for data it doesn't own. Never reaches another section's repository or store directly.

### Architecture tests (enforcement mechanism)

`tests/Humans.Application.Tests/Architecture/ProfileArchitectureTests.cs` enforces, via reflection on constructors:

- `ProfileService` lives in `Humans.Application.Services.Profile`.
- `ProfileService` constructor does not take `DbContext`.
- `ProfileService` constructor does not take `IMemoryCache`.
- `ProfileService` constructor takes `IProfileRepository` and `IProfileStore`.
- `IProfileRepository` lives in `Humans.Application.Interfaces.Repositories`.
- `IProfileStore` lives in `Humans.Application.Interfaces.Stores`.
- `CachingProfileService` lives in `Humans.Infrastructure.Services.Profiles`.
- Same rules for `ContactFieldService` and `VolunteerHistoryService`.

These are build-breaking. Any new service that drifts from the pattern fails CI.

### Roadmap status (`sequence:service-ownership` label)

| Phase | Issue | Status |
|---|---|---|
| 1 — Island services (Feedback, GA, CommPrefs, Consent, Legal) | #470 | CLOSED |
| 2 — Self-contained sections (Camps←CityPlanning, Shifts) | #471 | CLOSED |
| 3 — Ticket/Budget/Campaign triangle | #472 | CLOSED |
| 4 — Google integration | #473 | OPEN |
| 5 — Governance/Onboarding ↔ Profiles | #474 | OPEN |
| 6 — Core cross-cutting reads (TeamMembers, Users, Profiles) | #475 | OPEN |
| 7 — Jobs and account operations | #476 | OPEN |
| 8 — View components and controllers | #477 | OPEN |
| Practice spike — Governance | #503 | CLOSED |
| §15 Step 0 — Profile migration | #504 | OPEN (this PR) |
| §15 Step 1 — Quarantine cross-section reads | #510 | OPEN |
| Phase 2 — Remove GoogleWorkspaceSyncService direct writes | #492 | OPEN (on hold until 2026-04-19) |

Roughly half-complete. Expected to run parallel with new-module development over the next month.

---

## Part 4 — Impact on the original recommendations

### Already covered by PR #235 — de-prioritize

**#2 Structural guardrails.** `ProfileArchitectureTests.cs` *is* the analyzer, implemented as xUnit + reflection rather than Roslyn. Each section that migrates gets its own file. This is better than a centralized analyzer: it's section-scoped and lives alongside the code it validates. **Leave this alone; it's done right.**

**#3 ProfileController decomposition.** The PR already removes `HumansDbContext` injection and slims ProfileController as a side effect of moving services into the Application layer. Phase 8 explicitly targets controllers and view components. **Remove from active list** — it's being handled.

### Becomes *more* urgent

**#1 Integration tests.** The new pattern introduces three correctness hazards that unit tests cannot catch:

1. **Store ↔ DB divergence.** If the decorator forgets to call `_store.Upsert` after a write, reads return stale data indefinitely (no TTL, no safety net).
2. **Decorator composition.** If someone registers a service without `services.Decorate<>()`, caching silently disappears. Architecture tests check namespace, not DI graph.
3. **Cross-domain invalidation fan-out.** Profile writes trigger nav badge + notification meter + per-user cache invalidation + store refresh. Each migrated section adds more fan-out. Nothing verifies a write in section A invalidates the right stores in section B.

Testcontainers-backed integration tests per section — write → read-back-through-decorator → assert fresh data — catch all three.

**#3-as-scaffold (was half of #2/#3) — module template.** Flips from "nice to have" to **critical**. The new pattern has 7 files per section (repo interface + impl, store interface + impl, application service, caching decorator, architecture test), plus DI wiring, plus a warmup hosted service, plus cross-domain routing decisions.

Over the next month, new features will either (a) use the old simple pattern and immediately become §15-style migration debt, or (b) hand-copy from the Profile / Governance reference — error-prone given the Scrutor composition and invalidation choreography.

The template should generate all 7 files plus architecture tests plus DI registration, already wired to `ICacheKeyFactory` / `INavBadgeInvalidator` / `INotificationMeterProvider` conventions.

### Stay as-is

**#4 Authorization + #5 Launch gate.** PR #235 doesn't address either. Both still pay off per-module regardless of architecture.

---

## Part 5 — New recommendations that emerge from PR #235

### NEW-A — Decorator-integrity testing pattern

Architecture tests check structure. They don't check that `CachingProfileService.SaveProfileAsync` actually calls `_store.Upsert` after delegating. The bug class "decorator forgot to invalidate" is silent and subtle — stale data persists until process restart.

Build one canonical decorator test fixture: wire up the real `IProfileService` graph against an in-memory DB, write through the decorator, assert the store reflects the write, assert the nav badge was invalidated. Copy the fixture per section. Small cost; catches a whole category of bug that only surfaces in production.

### NEW-B — Cross-store invalidation contract

`CachingProfileService.RefreshStoreEntryAsync` today reaches into `IUserService` + `IUserEmailRepository` to rebuild a `CachedProfile` after any write. As more sections migrate (Teams, Governance), their stores will hold projections that depend on Profile data. When Profile changes, whose stores need to refresh? Currently nobody — deferred to §15 Step 1 ("Quarantine", #510).

This is heading toward an implicit domain-event system whether or not it's designed as one. Either:

- **Synchronous fan-out interfaces** — `IProfileInvalidationObserver` that other sections implement, wired via DI enumerable. Works at single-server scale, no infrastructure.
- **A tiny in-memory event bus** — `IDomainEvents.Publish(ProfileChanged)`, handlers subscribe.

At ~500 users synchronous fan-out is sufficient. The key is making the dependency **explicit** rather than letting each new decorator duplicate the stitching logic from `CachingProfileService`. Otherwise every section that launches adds 20–30 LOC of ad-hoc refresh code to other sections' decorators, and the matrix becomes unmaintainable by Phase 6.

---

## Part 6 — Revised top 5 for the next month

1. **Module scaffold for the repo/store/decorator pattern** — was #2/#3, now lead. Saves correctness bugs, not just time.
2. **Integration tests + decorator-integrity fixture** — was #1, expanded to include NEW-A. Only automated guard against the silent-stale-store bug class.
3. **Cross-store invalidation contract** — NEW-B. Small now, enormous if deferred to Phase 6.
4. **Authorization coverage enforcement** — unchanged. Still a gap.
5. **Unified module-launch gate** — unchanged. Now also checks "architecture test exists" and "uses new pattern" on any new module.

---

## Part 7 — Strategic outlook

PR #235 is **discipline-forcing work**: architecture tests are build-breaking, the pattern is explicit, and each phase closes one load-bearing section. The plan is credible and roughly half-complete.

The risk isn't the migration — it's **the next month**. §15 / Phase 4–8 migrations will run *concurrently* with 5–15 new feature modules.

- Without a scaffold and integration-test baseline, new modules will either adopt the old pattern (creating more migration debt) or adopt the new pattern inconsistently (creating correctness bugs architecture tests don't catch).
- With them, the two workstreams stay cleanly separated and compound correctly.

### One specific outlook callout

**GoogleWorkspaceSyncService migration (Phase 2, #492, on hold until 2026-04-19) will be the hardest.** 2,500 LOC with 21 methods spanning Drive/Groups/Admin APIs doesn't map cleanly to repo/store/decorator — there's no "table" to cache, just external API state.

That phase likely needs a **design variant**: an `IGoogleStateStore` that caches reconciliation state (permission maps, group memberships, folder hierarchies) rather than entity collections, plus a writer pattern that queues API operations through an outbox rather than executing synchronously. Worth designing **before** the unhold date, not after.

---

## Appendix — Raw data

### Service constructor dependency leaders

| Service | Deps |
|---|---|
| HumansMetricsService | 12 |
| OnboardingService | 11 |
| TeamService | 10 |
| ProfileService | 9 |
| OutboxEmailService | 9 |
| MagicLinkService | 9 |
| ConsentService | 9 |
| ApplicationDecisionService | 9 |
| TicketSyncService | 8 |
| TeamResourceService | 8 |
| RoleAssignmentService | 8 |
| FeedbackService | 8 |
| EmailProvisioningService | 8 |
| CampaignService | 8 |

### Top 10 files by commit frequency (last 60 days)

1. `src/Humans.Web/Resources/SharedResource.*.resx` — 80 commits (i18n)
2. `src/Humans.Infrastructure/Services/TeamService.cs` — 75
3. `src/Humans.Web/Controllers/TeamController.cs` — 65
4. `src/Humans.Web/Controllers/ProfileController.cs` — 63
5. `src/Humans.Infrastructure/Migrations/HumansDbContextModelSnapshot.cs` — 57 (auto-generated)
6. `src/Humans.Web/Controllers/AdminController.cs` — 56
7. `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs` — 45
8. `src/Humans.Web/Views/Shared/_Layout.cshtml` — 43
9. `src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs` — 43
10. `src/Humans.Web/Controllers/TeamAdminController.cs` — 42

### Largest services

| Service | LOC | Public methods |
|---|---|---|
| GoogleWorkspaceSyncService | 2,500 | 21 |
| TeamService | 2,379 | 48 |
| CampService | 1,290 | 45 |
| ProfileService | 1,219 | 28 |
| BudgetService | 1,162 | 31 |
| ShiftSignupService | 1,052 | — |
| ShiftManagementService | 996 | — |
| TicketQueryService | 904 | — |
| TeamResourceService | 727 | — |
| OnboardingService | 675 | — |
