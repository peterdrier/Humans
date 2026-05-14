# Section Align — Shifts
**Run started:** 2026-05-12 | **Mode:** existing-section | **Worktree:** `.worktrees/section-align-shifts`
**Branch:** `align/shifts` (off `origin/main` @ `09291512`)
**Canonical section name:** `Shifts` (no rename)

Phase 0 inventory across three axes per `.claude/skills/section-align/SKILL.md`:
1. **Boundary integrity** — clean section name, addressable routes/views/models, no cross-section DB access (except User ↔ Profile).
2. **Internal cohesion** — no EF leak from service layer, caching only in service layer, proper interfaces, reusable ViewComponents, architecture-test coverage.
3. **Test focus** — section-aligned tests in one canonical folder, 1-to-1 production-class to test-file mapping, coverage maps to invariants/negatives/triggers, redundancy pruned.

**Headline:** Shifts is **unusually clean** for its size. It's the most-migrated section in the codebase: zero inbound cross-section DB access, zero outbound cross-section DB access, zero `.Include` chains crossing domain boundaries, zero controller→DbContext leaks, and a documented caching pattern that already matches §15. The work in this PR is small and targeted: a test-folder reorganization, one missing architecture test file, several missing invariant/trigger tests, two doc-drift fixes, and a few interface-budget declarations. **No follow-up /section-align targets surfaced** — there's no supplier we depend on for any in-flight fix.

---

## Axis 1 — Boundary integrity

### 1.1 Section name consistency — **clean** ✓

Canonical name `Shifts` used consistently across folders, namespaces, controllers, views, ViewModels, extensions, roles, and routes. `Rota`, `Voluntell`, `VolunteerTracking`, `VolunteerEventProfile`, `EventSettings`, `GeneralAvailability` are sub-domain terms inside the section, not name variants.

### 1.2 Controller existence + route hosting — **clean** ✓

All 4 controllers exist and host their own routes:

| Controller | Class-level route | Range |
|---|---|---|
| `ShiftsController` | `[Route("Shifts")]` | `/Shifts/*` (browse, mine, signups, settings, orphan scan, preferences) |
| `ShiftAdminController` | `[Route("Teams/{slug}/Shifts")]` | `/Teams/{slug}/Shifts/*` (rota/shift admin, voluntell, signup decisions) |
| `ShiftDashboardController` | `[Route("Shifts/Dashboard")]` | `/Shifts/Dashboard/*` (cross-dept dashboard) |
| `VolunteerTrackingController` | `[Route("ShiftDashboard/[controller]")]` | `/ShiftDashboard/VolunteerTracking/*` |

`ShiftAdminController` lives under `/Teams/{slug}/Shifts/*` rather than `/Shifts/Admin/*`. The skill's "described drift" lens applies: the section doc describes this as intentional because coordinator context comes from the Team slug. The convention `/<Section>/Admin/*` could hold here (slug becomes a query param or route param on a `/Shifts/Admin/{slug}/...` prefix), but the URL pattern is well-established, deep-linked across views, and reflected in section doc routing. **[PROPOSED]** Keep as-is — convention exception accepted because coordinator UX flows from Teams nav and would otherwise duplicate the slug-resolution policy. Re-evaluate if Shifts ever introduces a non-team admin surface.

`VolunteerTrackingController.Route("ShiftDashboard/[controller]")` resolves to `/ShiftDashboard/VolunteerTracking/*` — note `ShiftDashboard` (no `s`), inconsistent with `Shifts/Dashboard` on the dashboard controller. **[PROPOSED]** Phase 1 fix: change to `[Route("Shifts/Dashboard/VolunteerTracking")]` to match `ShiftDashboardController` and stay under the `/Shifts/*` umbrella.

### 1.3 URL surface — **near-clean** (one route prefix mismatch)

10 routes on `ShiftsController`, 14 on `ShiftAdminController`, 3 on `ShiftDashboardController`, 4 on `VolunteerTrackingController`. No `/Admin/Shifts/*` paths. No URL aliases. No Shifts data hosted on `BoardController` / `TeamsController` / `ProfileController` / `GoogleController` / `AccountController` / `AdminController` / `HomeController`.

Only flag: the `/ShiftDashboard/VolunteerTracking/*` prefix (1.2) — same fix.

### 1.4 Views folder — **clean** ✓

`Views/Shifts/`, `Views/ShiftAdmin/`, `Views/ShiftDashboard/`, `Views/VolunteerTracking/` all exist. Shifts page views are inside those folders; partials in `Views/Shared/` are genuine cross-page widgets (`_EventRotaTable`, `_BuildStrikeRotaTable`, `_RotaHeader`, `_StaffingChart`, `_StaffingHoursChart`, `_DashboardStats`, `_DashboardStaffing`, `_ShiftsSummaryCard`, `_VolunteerSearchScript`, `_VolunteerProfileBadges`). `ShiftSignups` ViewComponent partials at `Views/Shared/Components/ShiftSignups/` (correct).

### 1.5 ViewModel placement — **clean** ✓

`Models/ShiftViewModels.cs`, `Models/Shifts/`, `Models/UpcomingShiftEntry.cs`. No Shifts types in `Models/AdminViewModels.cs` or grab-bag files. `Models/OnboardingWidget/ShiftsStepViewModel.cs` etc. correctly live with the consumer (Onboarding widget).

### 1.6 Controller-base leak — **clean** ✓

`HumansControllerBase.cs` contains only generic user-resolution and TempData helpers. No `Shift|Rota|Voluntell|VolunteerTracking|EventSettings|GeneralAvailability` types.

### 1.7 Extensions placement — **clean** ✓

`Extensions/Sections/ShiftsSectionExtensions.cs` exists. No Shifts-related extension files in `Extensions/` root.

### 1.8 Role surface — **clean** ✓

Roles touching Shifts: `Admin`, `NoInfoAdmin` (correctly `*Admin`-suffixed), `VolunteerCoordinator`. Section-specific policies (`ShiftDashboardAccess`, `ShiftDepartmentManager`, `VolunteerTrackingWrite`) are correctly named and resolve to combinations of these roles + coordinator status from `ITeamService`.

### 1.9 Inbound cross-section DB access — **clean** ✓

Grep across `src/` (excluding `Repositories/Shifts/`, `Services/Shifts/`, `Configurations/Shifts/`) for `_db.{Rotas|Shifts|ShiftSignups|EventSettings|GeneralAvailability|VolunteerEventProfiles|ShiftTags|VolunteerTagPreferences|VolunteerBuildStatuses}` (plus `_context.` / `ctx.` variants): **0 hits.**

### 1.10 Inbound EF navigations — **clean** (with one expected outbound FK from a consumer)

No non-Shifts entity declares a nav into a Shifts entity. The one exception is `EmailOutboxMessage.ShiftSignup?` — a legitimate consumer-side nav for outbound notification dispatch, which is FK-only by typed-FK pattern in the Email section's configuration. Not a violation.

### 1.11 Outbound cross-section access — **clean** ✓

Grep `Repositories/Shifts/` and `Services/Shifts/` for non-Shifts DbSets and cross-domain `.Include` chains: **0 hits.** All cross-section access routes through service interfaces (`ITeamService`, `IUserService`, `IRoleAssignmentService` lazy, `ITicketQueryService` lazy, `IAuditLogService`, `INotificationService`) as documented in the section doc's Cross-Section Dependencies block.

Every `.Include` in Shifts repos chains aggregate-locally (Rota→Shifts→ShiftSignups, ShiftSignup→Shift→Rota→EventSettings, etc.). `EventParticipationConfiguration.cs` lives under `Configurations/Shifts/` but configures a Users-owned entity that Shifts reads via `IUserService` (read-only consumer; not a write path).

### 1.12 Controller → DbContext — **clean** ✓

All 4 controllers: 0 references to `HumansDbContext`, `DbSet<`, `_db.`, or `_context.`.

### 1.13 Migrations — **clean** ✓

Sampled the 5 most recent Shifts migrations (2026-03-16 through 2026-03-30). All EF-auto-generated. Only manual content is `migrationBuilder.InsertData` seeding 8 shift tags in `20260330230256_AddShiftTagsAndVolunteerPreferences.cs` — legitimate, documented as the seed source for `ShiftTagConfiguration`.

### 1.14 Section invariant doc shape — **1 doc-drift item**

`docs/sections/Shifts.md` has every required heading from `SECTION-TEMPLATE.md`. `VolunteerBuildStatus` and `VolunteerTracking` are documented in Data Model and Routing.

**Drift:** Lines 278–279 omit one service and one table from the Architecture footer:

| Footer field | Current | Should be |
|---|---|---|
| Owning services | `ShiftManagementService`, `ShiftSignupService`, `GeneralAvailabilityService` | + `VolunteerTrackingService` |
| Owned tables | `rotas`, `shifts`, `shift_signups`, `event_settings`, `general_availability`, `volunteer_event_profiles`, `shift_tags`, `volunteer_tag_preferences`, `rota_shift_tags` | + `volunteer_build_statuses` |

`design-rules.md §8` carries the same omission (per the rule "ownership stated twice; they drift in practice — do not let that drift reach a PR"). Both fix in Phase 4.

### 1.15 Prior review items — N/A (existing-section mode, no PR)

---

## Axis 2 — Internal cohesion

### 2.1 EF leakage from service layer — **clean** ✓

`Services/Shifts/{ShiftManagementService,ShiftSignupService,GeneralAvailabilityService,VolunteerTrackingService}.cs`: zero `using Microsoft.EntityFrameworkCore`, zero `IQueryable`, zero `DbContext`/`DbSet`, zero `.Include`/`.AsNoTracking`/`.ToListAsync`/`.FirstOrDefaultAsync`/`.SaveChangesAsync` in service bodies. All DB access routes through repositories.

Architecture tests pin this for three of the four services:
- ✅ `ShiftManagementService_HasNoDbContextConstructorParameter`
- ✅ `ShiftSignupService_HasNoDbContextConstructorParameter`
- ✅ `GeneralAvailabilityService_HasNoDbContextConstructorParameter`
- ❌ **Missing for `VolunteerTrackingService`** — no `VolunteerTrackingArchitectureTests.cs` file exists. (Phase 2 fix.)

### 2.2 Caching placement — **clean** ✓ (matches §15 doc)

| Location | Cache usage | Status |
|---|---|---|
| `ShiftManagementService.cs` | `IMemoryCache` injected; `shift-auth:{userId}` (60s) + dashboard caches (5min sliding) | ✅ Sanctioned per Shifts.md §Architecture; invalidation via `IShiftAuthorizationInvalidator` |
| `ShiftSignupService.cs` | none | ✅ Option A — request-scoped reads |
| `GeneralAvailabilityService.cs` | none | ✅ Option A — small surface |
| `VolunteerTrackingService.cs` | none | ✅ |
| All 4 Shifts repositories | none | ✅ |
| All 4 Shifts controllers | none | ✅ |
| `ShiftSignupsViewComponent.cs` | none | ✅ |

### 2.3 DI lifetimes — **clean** ✓

`Extensions/Sections/ShiftsSectionExtensions.cs`:

| Binding | Lifetime | Backing | OK |
|---|---|---|---|
| `IShiftManagementRepository → ShiftManagementRepository` | Singleton | `IDbContextFactory<HumansDbContext>` | ✅ |
| `ShiftManagementService` | Scoped | (concrete) | ✅ |
| `IShiftManagementService → ShiftManagementService` | forward | — | ✅ |
| `IShiftAuthorizationInvalidator → ShiftManagementService` | forward | — | ✅ external-section invalidation hook |
| `IUserMerge → ShiftManagementService` | forward | — | ✅ account-merge fan-out |
| `IShiftSignupRepository → ShiftSignupRepository` | Scoped | `HumansDbContext` direct | ✅ multi-step mutations share change-tracker |
| `ShiftSignupService` | Scoped | — | ✅ |
| `IShiftSignupService → ShiftSignupService` | forward | — | ✅ |
| `IUserDataContributor → ShiftSignupService` | forward | — | ✅ GDPR export contributor |
| `IUserMerge → ShiftSignupService` | forward | — | ✅ |
| `IGeneralAvailabilityRepository → GeneralAvailabilityRepository` | Singleton | `IDbContextFactory` | ✅ |
| `ShiftGeneralAvailabilityService` | Scoped | — | ✅ |
| `IGeneralAvailabilityService → ShiftGeneralAvailabilityService` | forward | — | ✅ |
| `IUserMerge → ShiftGeneralAvailabilityService` | forward | — | ✅ |
| `IVolunteerTrackingRepository → VolunteerTrackingRepository` | Scoped | `HumansDbContext` direct | ✅ multi-step build-status mutations |
| `IVolunteerTrackingService → VolunteerTrackingService` | Scoped | — | ✅ |

### 2.4 Repository pattern — **clean** ✓

All 4 repositories are `sealed`, live under `Infrastructure/Repositories/Shifts/`, and use the lifetime-appropriate context source. No Shifts entity is append-only (per §12), so no Update/Delete restrictions apply.

### 2.5 Shared visual components — **clean** ✓

- **ViewComponents:** `ShiftSignupsViewComponent` (used by Profiles, Shifts/Mine, ShiftDashboard) — correctly placed; calls services, no DbContext.
- **TagHelpers:** none Shifts-specific.
- **Partials:** all listed in 1.4. None are masquerading page views; none are cross-section reuse candidates better expressed as ViewComponents.

### 2.6 Interface budgets — **2 declared, 3 missing**

| Interface | Public methods | `[SurfaceBudget(N)]` | Action |
|---|---|---|---|
| `IShiftManagementService` | 48 | `[SurfaceBudget(48)]` | ✅ |
| `IShiftSignupService` | 23 | — | **Phase 2: add `[SurfaceBudget(23)]`** |
| `IGeneralAvailabilityService` | 4 | — | **Phase 2: add `[SurfaceBudget(4)]`** |
| `IVolunteerTrackingService` | 5 | — | **Phase 2: add `[SurfaceBudget(5)]`** |
| `IShiftAuthorizationInvalidator` | 1 | n/a | not needed at this size |

Method-shape analysis (do any of the large interfaces have status-split or bag-of-flags anti-patterns?):

- **`IShiftManagementService` (48):** the breakdown is reasonable — event settings (4), rotas (5), shifts (4), urgency (2), staffing/summary (6), dashboard (6), bulk creation (2), tags/profiles (3), search (1), authorization (3), utility (2). Several reads are DB-predicate-pushed because Shifts is the documented scale exception (thousands of signups across many events; per-row work in RAM would be wasteful). **No consolidation proposed.**
- **`IShiftSignupService` (23):** state-machine (8) + range ops (4) + reads (6) + helpers (5). All methods have distinct semantics. **No consolidation proposed.**

No inter-service overlap detected (no Service-returning-entity vs Viewer-returning-DTO pair on the same reads).

### 2.7 Architecture test coverage — **1 missing file + several generic-test candidates**

Existing section-specific test files:
- `ShiftManagementArchitectureTests.cs` — 7 tests, covers namespace, no-DbContext, no-cache, takes repo, repo sealed, cross-domain nav strip on `Rota`/`ShiftSignup`/`VolunteerEventProfile`/`VolunteerTagPreference`, `IShiftAuthorizationInvalidator` implementation.
- `ShiftSignupArchitectureTests.cs` — 9 tests, similar shape for `ShiftSignupService`.
- `GeneralAvailabilityArchitectureTests.cs` — 9 tests, similar shape for `GeneralAvailabilityService`.

**Missing — `VolunteerTrackingArchitectureTests.cs`:** Phase 2 creates this file mirroring `ShiftSignupArchitectureTests.cs`:
- `VolunteerTrackingService_LivesInHumansApplicationServicesShiftsNamespace`
- `VolunteerTrackingService_HasNoDbContextConstructorParameter`
- `VolunteerTrackingService_HasNoIMemoryCacheConstructorParameter`
- `VolunteerTrackingService_TakesRepository`
- `IVolunteerTrackingRepository_LivesInApplicationInterfacesRepositoriesNamespace`
- `VolunteerTrackingRepository_IsSealed`

**Phase 3 candidates for promotion to generic rules** (under `tests/Humans.Application.Tests/Architecture/Rules/`): the "service has no DbContext ctor param", "service has no IMemoryCache ctor param" (with allow-list), "service lives in `Humans.Application.Services.<Section>`", "repository is sealed", "repository lives in `Humans.Infrastructure.Repositories.<Section>`" patterns are duplicated across most section-specific arch test files. Consolidating into reflective generic rules removes per-section copies (Shifts contributes 4 services × ~3 generic-shape methods = ~12 lines we can drop once the generic rule lands). Section-specific tests then keep only genuinely-section-specific invariants: cross-domain nav-strip table, single-writer DbSet check, `IShiftAuthorizationInvalidator` impl.

---

## Axis 3 — Test focus

### 3.1 Test folder placement — **drift: 7 Shifts service test files in parent `Services/`**

Canonical home for Shifts service tests: `tests/Humans.Application.Tests/Services/Shifts/`. Currently 4 files live there; **7 misplaced** in the parent `Services/` folder:

| Current path | Should be |
|---|---|
| `Services/ShiftSignupServiceTests.cs` | `Services/Shifts/ShiftSignupServiceTests.cs` |
| `Services/ShiftManagementServiceTests.cs` | `Services/Shifts/ShiftManagementServiceTests.cs` |
| `Services/ShiftDashboardMetricsTests.cs` | `Services/Shifts/ShiftDashboardMetricsTests.cs` |
| `Services/GeneralAvailabilityServiceTests.cs` | `Services/Shifts/GeneralAvailabilityServiceTests.cs` |
| `Services/ShiftSignupServiceEarlyEntryTests.cs` | `Services/Shifts/ShiftSignupServiceEarlyEntryTests.cs` |
| `Services/ShiftSignupsBucketingTests.cs` | `Services/Shifts/ShiftSignupsBucketingTests.cs` |
| `Services/ShiftUrgencyTests.cs` | `Services/Shifts/ShiftUrgencyTests.cs` |

Already correctly placed: `VolunteerTrackingServiceTests.cs`, `ShiftSignupServiceFilterIncompleteOnboardingTests.cs`, `ShiftSignupServiceForcePendingTests.cs`, `PromoteWidgetPendingSignupsAfterAdmissionTests.cs`. Repository tests live correctly under `Repositories/` / `Repositories/Shifts/`. Architecture tests, controller tests, domain entity tests, viewmodel tests all correctly categorized.

Phase 1 mechanical move (7 `git mv` + namespace updates).

### 3.1a 1-to-1 production class ↔ test file — **partial**

Most services and repos have a matching test file. Helper classes that don't have a dedicated test file but have indirect coverage through the services that use them:

| Production class | Test file | Verdict |
|---|---|---|
| `ShiftManagementService` | `ShiftManagementServiceTests.cs` (+ several feature-specific siblings) | ✅ |
| `ShiftSignupService` | `ShiftSignupServiceTests.cs` (+ 4 feature siblings) | ✅ multi-file by feature is fine; main file remains the umbrella |
| `GeneralAvailabilityService` | `GeneralAvailabilityServiceTests.cs` | ✅ |
| `VolunteerTrackingService` | `VolunteerTrackingServiceTests.cs` | ✅ |
| `ShiftManagementRepository` | `ShiftManagementRepositoryTests.cs` | ✅ |
| `ShiftSignupRepository` | `ShiftSignupRepositoryTests.cs` | ✅ |
| `GeneralAvailabilityRepository` | none (no dedicated unit test) | covered via service test + integration; ❎ not blocking |
| `VolunteerTrackingRepository` | `Integration.Tests/Repositories/Shifts/VolunteerTrackingRepositoryTests.cs` | ✅ (integration layer) |
| `BuildSubPeriodClassifier` | none — covered indirectly via dashboard metrics tests | **Phase 2: add `BuildSubPeriodClassifierTests.cs`** (pure mapping, easy unit test) |
| `ShiftRoleChecks` | none — covered via controller tests | indirectly covered, not blocking |
| `ShiftVolunteerSearchBuilder` | none — covered via controller tests | indirectly covered, not blocking |
| `Shift` (entity) | `Domain.Tests/Entities/ShiftTests.cs` | ✅ |
| `ShiftSignup` (entity, state machine) | `Domain.Tests/Entities/ShiftSignupTests.cs` | ✅ |
| `Rota`, `EventSettings`, `GeneralAvailability`, `VolunteerEventProfile`, `VolunteerBuildStatus`, `ShiftTag`, `VolunteerTagPreference` (entities) | none — behavior tested via service tests | acceptable; entities are data-shape-heavy with limited behavior |

Bundled test-file drift: none observed.

### 3.2 Coverage map vs section doc — **9 coverage holes**

Invariants/Negative Access Rules/Triggers with no direct test coverage. **Phase 2 adds for the high-value ones:**

- ⚠️ **Event settings singleton** — `CreateAsync`/`UpdateAsync` reject second `IsActive=true` (Invariants line 229). No test found.
- ⚠️ **Medical data gating** — `GetShiftProfileAsync(uid, includeMedical=false)` strips medical fields (Invariants line 231; Negative Access Rules lines 251/253). No test found.
- ⚠️ **Rota delete rejects with Confirmed signups** (Triggers line 262). No test found.
- ⚠️ **Rota move audit** — `AuditAction.RotaMovedToTeam` + `Rota.TeamId` targeted update (Triggers line 261). No test found.
- ⚠️ **ShiftCoverageGap notification on Bail/Remove below MinVolunteers** (Triggers line 259). No test found.
- ⚠️ **Dashboard filter period-vs-date-range mutex** — "when both arrive, period wins" (Invariants line 237). No test found.
- ⚠️ **BuildSubPeriod narrowing only when `period == Build`** (Invariants line 238). No test found.
- ⚠️ **DevelopmentDashboardSeeder gating** — `IsDevelopment` AND `DevAuth:Enabled` AND `ShiftDashboardAccess` (Invariants line 239). No test found.
- ⚠️ **Pending → Confirmed auto-promotion via `PromoteWidgetPendingSignupsAfterAdmissionAsync`** (Invariants line 240) — partial coverage in `PromoteWidgetPendingSignupsAfterAdmissionTests.cs`; verify happy-path + edge cases there are sufficient before declaring covered.

Well-covered already: state machine transitions, MaxVolunteers capacity ceiling, voluntelling shape, range signups atomicity, early-entry freeze, signup overlap (including all-day window math at 08:00–18:00), shift browsing closed.

### 3.3 Redundancy / over-testing — **2 minor brittleness items**

Two assertions that should be tightened (Phase 2/3 trivial fix):

| File:line | Issue |
|---|---|
| `ShiftSignupServiceTests.cs:~167–169` (`SignUp_AllDayShiftAfterPriorNightWatch_DoesNotFalselyConflict`) | `result.Error.Should().BeNull()` — passes on success OR an unrelated null-error path. Replace with `result.Success.Should().BeTrue()`. |
| `ShiftSignupServiceTests.cs:~192–193` (`SignUp_SameDayEarlyShiftBeforeAllDay_DoesNotFalselyConflict`) | Same. |

No near-duplicate `[Theory]` over-parametrization. No framework-behavior testing. No private-state reflection. No mock-graph spaghetti calling out for rewrite.

### 3.4 Test-to-section ratio — **proportionate**

Section production ~5.5k LOC; test code ~7k LOC across 21 files. Ratio 1.29× is reasonable for a section with a state machine, multi-table atomic operations, and a coordinator dashboard. No "over-tested" verdict.

### 3.5 Brittleness signals — **clean**

- ✅ All tests use `FakeClock` (no `DateTime.Now`).
- ✅ Seed helpers (`SeedShift`, `SeedSignup`, `SeedRotaAsync`) reduce duplication.
- ✅ InMemory DbContext is per-test via `Guid.NewGuid().ToString()`.
- ✅ No reflection abuse, no private-state mutation.
- ✅ No real HTTP/SMTP/Google API calls.

Minor: no `[Slow]` markers on `ShiftDashboardMetricsTests.cs` (~65 KB, ~100 tests). Add markers in Phase 3 if any individual test exceeds 100 ms on local runs.

### 3.6 Mutation testing — **no Shifts-targeted run on disk**

`local/stryker-runs/shifts/` does not exist. Configs exist (`stryker-smoke-config.json`, `stryker-profiles-config.json`) but no Shifts-narrowed profile. **[PROPOSED]** Phase 3 (optional): author `stryker-shifts-config.json` mirroring the profiles one, run against the Shifts production paths, and feed the report into `scripts/analyze-test-utility.ps1`. Surviving mutants targeting invariants/negatives/triggers from §3.2 become Phase 2 test adds; high-confidence test-debt candidates become Phase 3 deletes. Skip if Peter prefers to defer — none of the Phase 2 work is gated on Stryker.

---

## Test-attribute gate (per `docs/testing/mutation-testing.md`)

| Metric | Value |
|---|---|
| Baseline (last gate update) | (not consulted in Phase 0 — capture before Phase 3) |
| Projected net delta in this PR | **+8 to +12** (new invariant/trigger tests, new arch test file, new helper-class test) and **−2** (deleted/tightened brittle assertions) → **net +6 to +10** |
| Justification | Net positive is intentional: 9 invariants/triggers documented in the section doc currently have no direct test. Phase 3 may delete redundant cases identified via Stryker; that pass can rebalance the delta down. |

---

## Stop conditions tripped

**None.** No name collision; no cross-section DB budget bump (zero cross-section access in either direction); no ownership ambiguity; doc shape is intact (only the Architecture footer drifts on one service + one table); branch is clean; no outbound API gap on a consumer-side fix because there are no consumer-side fixes to make.

---

## Follow-up /section-align targets

**None.** Shifts has zero inbound and zero outbound cross-section DB or `.Include` coupling. Cross-section calls flow through public service interfaces (`ITeamService`, `IUserService`, `IRoleAssignmentService`, `ITicketQueryService`, `IAuditLogService`, `INotificationService`) and that surface is healthy on every supplier side as far as Shifts is concerned. This is the model alignment state.

---

## Phase plan

### Phase 1 — Surface alignment (mechanical, Sonnet)

1. **VolunteerTrackingController route fix** — change `[Route("ShiftDashboard/[controller]")]` to `[Route("Shifts/Dashboard/VolunteerTracking")]`. Update any `RedirectToAction` / tag-helper-link sites + tests. (1.2 / 1.3)
2. **Test folder reorganization** — `git mv` the 7 misplaced test files from `Services/` to `Services/Shifts/`; update namespaces in moved files. (3.1)
   - `ShiftSignupServiceTests.cs`
   - `ShiftManagementServiceTests.cs`
   - `ShiftDashboardMetricsTests.cs`
   - `GeneralAvailabilityServiceTests.cs`
   - `ShiftSignupServiceEarlyEntryTests.cs`
   - `ShiftSignupsBucketingTests.cs`
   - `ShiftUrgencyTests.cs`
3. Build + tests green after each step. Commit per logical unit.

### Phase 2 — Architecture tests + missing coverage (Sonnet→Opus)

1. **Create `tests/Humans.Application.Tests/Architecture/VolunteerTrackingArchitectureTests.cs`** with 6 tests mirroring `ShiftSignupArchitectureTests.cs`. (2.7)
2. **Add `[SurfaceBudget(N)]` declarations** on `IShiftSignupService` (23), `IGeneralAvailabilityService` (4), `IVolunteerTrackingService` (5). Run `InterfaceMethodBudgetTests` to confirm budget ratchet enforces. (2.6)
3. **Add `BuildSubPeriodClassifierTests.cs`** under `tests/Humans.Application.Tests/Domain/Helpers/` (or wherever the production class lives) — pure-mapping tests for each `BuildSubPeriod` boundary. (3.1a)
4. **Add invariant/trigger tests** for the 8 holes in §3.2:
   - Event settings singleton rejection
   - Medical data gating (`GetShiftProfileAsync(uid, false)` strips fields; `(uid, true)` returns them)
   - Rota delete rejects when Confirmed signups present
   - Rota move emits `AuditAction.RotaMovedToTeam` and targeted-updates `TeamId`
   - Bail/Remove below `MinVolunteers` fires `ShiftCoverageGap`
   - Dashboard filter mutex (period wins when both arrive)
   - BuildSubPeriod ignored unless `period == Build`
   - `DevelopmentDashboardSeeder` gated by `IsDevelopment` + `DevAuth:Enabled` + `ShiftDashboardAccess`
5. **Tighten 2 brittle assertions** — replace `result.Error.Should().BeNull()` with `result.Success.Should().BeTrue()`. (3.3)
6. **Doc-drift fixes** — update Shifts.md lines 278–279 and design-rules.md §8 in the same commit. (1.14)

### Phase 3 — /simplify pass + test pruning (Opus)

Scope: small. The section is already clean. Candidates:

1. **Promote duplicated architecture-test patterns to generic rules** in `tests/Humans.Application.Tests/Architecture/Rules/`:
   - `Application_Services_TakeNoDbContext` (reflective scan)
   - `Application_Services_TakeNoIMemoryCache_UnlessAllowed` (with allow-list including `ShiftManagementService`)
   - `Application_Services_LiveInHumansApplicationServicesNamespace`
   - `Application_Repositories_AreAllSealed`
   - `Application_Repositories_LiveInHumansInfrastructureRepositoriesNamespace`
   - Trim the Shifts-specific arch test files to retain only Shifts-specific invariants (nav-strip table on Shifts-owned entities, `IShiftAuthorizationInvalidator` impl). (2.7)
2. **Mutation testing (optional)** — author `stryker-shifts-config.json` and run; surface high-confidence prune candidates. (3.6) — Peter to decide whether to include in this PR or skip.
3. **`[Slow]` markers** on dashboard-metrics tests if profiling shows >100 ms cases. (3.5)

### Phase 3 — outcome (Stryker run, 2026-05-12)

Item 1 (generic arch-rule promotion) **deferred** — touches every section's arch test file, properly its own cross-section refactor.

Item 2 (Stryker pass) **done**. Config at `tests/Humans.Application.Tests/stryker-shifts-config.json`. Run command:

```
cd tests/Humans.Application.Tests
dotnet tool run dotnet-stryker --config-file stryker-shifts-config.json --output ../../local/stryker-runs/shifts
```

Runtime: 6m34s. Mutation level Standard. Mutate glob: `**/Services/Shifts/*.cs` (4 files). Of 13,021 mutants generated, 1,267 were tested (rest skipped by Stryker filters or compile-error from mutation conflicts). Top-line score: **10.73%**.

Per-file breakdown (Killed / Survived / CompileError / Ignored / Timeout):

| Service | Killed | Survived | CompileError | Ignored | Timeout |
|---|---:|---:|---:|---:|---:|
| `GeneralAvailabilityService.cs` | 1 | 2 | 0 | 0 | 0 |
| `ShiftManagementService.cs` | 130 | 482 | 187 | 95 | 5 |
| `ShiftSignupService.cs` | **0** | 513 | 73 | 75 | 0 |
| `VolunteerTrackingService.cs` | **0** | 134 | 36 | 8 | 0 |

**`ShiftSignupService.cs` and `VolunteerTrackingService.cs` showing 0 killed mutants is almost certainly the MTP-runner test-discovery bug** that `docs/testing/mutation-testing.md` already warns about (`Stryker's MTP runner currently does not reliably honor test-case-filter in this project`). Both services have substantial behavior tests that pass when run directly via `dotnet test`. A genuine 0-killed across 513 mutants would mean the tests don't exercise the service at all — falsified by the Phase 2B run where `ShiftCoverageGap_FiresOnBail_*` and the medical-gating tests pass against real service code. Treat these two services' Stryker scores as **unreliable** until the MTP runner discovery issue is fixed.

**`ShiftManagementService.cs` Stryker results ARE trustworthy.** 482 surviving mutants in 1500 LOC of production code, with 130 killed (21% kill rate). Top survivor categories:
- 174 Equality mutations (boundary checks, status comparisons, capacity math)
- 53 Statement mutations (block removal — high-signal undertested code)
- 45 String mutations (mostly log messages — low signal)
- 31 Logical mutations (`&&`/`||` swaps)
- 23 Arithmetic, 22 Negate-expression, 30 Conditional

Sampled high-value survivors (file:line, `ShiftManagementService.cs`) that **do not map to existing tests** and would warrant coverage in a dedicated follow-up:
- L197–205 — early-entry-allocation step function boundaries (`key > dayOffset`, `key >= applicableKey`, `applicableKey == int.MinValue` sentinel).
- L220–222 — team is-system / SystemTeamType validation on rota move target.
- L246–252 — `targetTeam.ParentTeamId is null`, `rota.TeamId != targetTeamId`, system-team-type rejection on rota move.
- L281, L289 — `confirmedCount >= 0`, `d.Status != SignupStatus.Pending` filter.
- L350–360 — period boundaries (`rota.Period != RotaPeriod.Build`, `dayOffset > es.BuildStartOffset`, etc.) for shift creation.

**Decision:** addressing the 482 ShiftManagementService survivors is **out of section-align scope** — it would unbounded-grow the test suite (likely 50–100 new tests). The Stryker artifact + config are committed so a dedicated `test(shifts): close ShiftManagementService Stryker coverage gaps` follow-up can pick this up. The section doc may also need invariants added that pin the system-team / parent-team validation rules currently only implicit in code.

Item 3 (`[Slow]` markers) **deferred** — requires profiling timing data that wasn't captured in this run; trivial to add later.

### Phase 4 — Doc polish (Opus)

1. **Section invariant doc** — verify `Shifts.md` accurately describes the new test placements, the new arch test file, and the corrected `VolunteerTrackingController` route. Confirm Architecture footer correctness.
2. **`docs/architecture/dependency-graph.md`** — Shifts edges should not change (no new cross-section calls added, no removed ones), but verify before declaring done.
3. **`docs/architecture/maintenance-log.md`** — record the section-align run.
4. **`/freshness-sweep`** — Shifts.md is in the freshness catalog; run sweep to check drift on touched docs.
5. **Final bot-review sub-loop** until merge-ready.
6. **Closing /section-align re-run** in the same worktree — should return "clean" with no new findings.
