# Shifts — G0 First Audit

**Section:** Shifts · **Kind:** vertical · **Audited:** 2026-08-03 @ 5a9bbe198

> Note: nobodies-collective/Humans#809 (EventSettings entity-leak → `BurnSettingsInfo` DTO migration) is being **partially fixed in a parallel lane tonight**. This audit records the state as observed at read time — expect this file to need a re-check once that lane lands.

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository in-section | PASS | `reforge ownership-violations --owner Shifts --tables rotas,shifts,shift_signups,event_settings,general_availability,volunteer_event_profiles,volunteer_build_statuses,shift_tags,volunteer_tag_preferences,rota_shift_tags` → **0 violations**. Doc confirms the #882 convergence: `IVolunteerTrackingRepository` now owns only `general_availability`+`volunteer_build_statuses`; the Build-period signup reads it formerly duplicated were converged onto `IShiftManagementRepository`. |
| 2 | One writer-service per table | PASS | Single `ShiftRepository` (behind `IShiftManagementRepository`) + `VolunteerTrackingRepository` (behind `IVolunteerTrackingRepository`), no interceptor pattern found for this section. |
| 3 | No EF entity leaks across boundary | **FAIL (in-flight remediation, #809)** | `ApplicationServiceEntityReadReturns.baseline.txt` carries **7 rows** for this section: `IShiftManagementService.GetActiveAsync/GetByIdAsync → EventSettings`, `GetBrowseShiftsAsync/GetShiftByIdAsync/GetUrgentShiftsAsync → Shift`, `GetOrCreateShiftProfileAsync/GetShiftProfileAsync → VolunteerEventProfile`, `GetRotaByIdAsync/GetRotasByDepartmentAsync → Rota`, plus `IShiftSignupService.GetByUserAsync → ShiftSignup`. Current state: the clean replacement surface (`IBurnSettingsService.GetActiveAsync/GetByIdAsync → BurnSettingsInfo`, issue #719) already exists and is live (confirmed: `src/Humans.Application/Interfaces/Shifts/BurnSettingsInfo.cs`, `IBurnSettingsService.cs`), but the **old leaking `IShiftManagementService.GetActiveAsync/GetByIdAsync` methods are still present and still baselined** — the migration is additive-not-yet-subtractive. The other 5 entity-leak rows (Shift/Rota/VolunteerEventProfile/ShiftSignup) are outside #809's scope (that issue targets `EventSettings` specifically) and remain open debt. |
| 4 | No cross-section EF joins | PASS | No `CrossSectionEfJoinAnalyzer` baseline entries for Shifts. |
| 5 | No `[Obsolete]` navs / `[Grandfathered]` / owned baseline rows (or queued G2 item) | **PARTIAL** | Cross-domain navs are properly stripped (`Rota.Team`, `ShiftSignup.User`/`EnrolledByUser`/`ReviewedByUser`, `VolunteerEventProfile.User`, `VolunteerTagPreference.User`, `GeneralAvailability.User` — all FK-only per the doc). But the 7 `ApplicationServiceEntityReadReturns` baseline rows above are owned here with only 1 of 7 having an active remediation lane (#809, partial). |
| 6 | Controllers thin (no HUM0031 grandfathers) | **FAIL (tracked)** | `ShiftsController.cs` carries 1 `[Grandfathered(ruleId: "HUM0031", …)]` ("Worst-offender at HUM0031 introduction: 38 statements, cc 20", `issueRef: "nobodies-collective/Humans#857"`). `ShiftAdminController`, `ShiftDashboardController`, `VolunteerTrackingController` are clean (not in the HUM0031 grep hit list). |
| 7 | `docs/sections/Shifts.md` current | PASS (high confidence) | Extremely detailed and current — references #882 repo convergence, #720 `IShiftView` cache with T-09/T-10 migration waves, #541 nav-strip. |

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests real Postgres, zero EF-InMemory | **FAIL** | `ShiftRepositoryManagementTests.cs:30`, `ShiftRepositorySignupTests.cs:30`, `ShiftRepositorySummaryTests.cs:39` all use `.UseInMemoryDatabase(...)`. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | **FAIL (widespread)** | 12 files under `tests/Humans.Application.Tests/Services/Shifts/` reference `ServiceTestHarness` (itself `UseInMemoryDatabase`-backed): `ShiftDashboardMetricsTests.cs`, `ShiftManagementServiceCoveragePiesTests.cs`, `ShiftManagementServiceTests.cs`, `ShiftSignupRepositoryActiveCommittedTests.cs`, `ShiftSignupServiceAutoConfirmIgnoresConsentTests.cs`, `ShiftSignupServiceCalendarFeedTests.cs`, `ShiftSignupServiceCoverageGapTests.cs`, `ShiftSignupServiceEarlyEntryTests.cs`, `ShiftSignupServiceTests.cs`, `ShiftSummaryServiceTests.cs`, `VolunteerTrackingAvailabilityTests.cs`, `Workload/WorkloadServiceTests.cs`. `ShiftDashboardMetricsTests.cs` even hand-rolls `FakeTicketQueryService`/`FakeUserService`/`FakeTeamService` classes that take `HumansDbContext` directly and read off the *same* in-memory context as the repository under test, rather than mocking `ITicketServiceRead`/`IUserServiceRead`/`ITeamServiceRead` (the code comment admits this is a deliberate compromise "so existing DbContext-based test seed helpers still drive the scenarios end-to-end"). This is the largest single-section G3.2 gap found across the whole batch. |
| 3 | Invariants/triggers each have a test | PASS (spot-check) | `MaxVolunteers` capacity ceiling has direct hits in `ShiftManagementServiceTests.cs` and others. Given the sheer size of the test surface (12+ files), coverage of the doc's ~20 invariants is plausible but not exhaustively traced. |
| 4 | No skipped tests without issue ref | PASS (tentative) | No hits found. |
| 5 | Tests grouped under section | PASS | All under `tests/Humans.Application.Tests/Services/Shifts/` and `Repositories/Shift*Tests.cs` — good section grouping (better than Profiles/Users/Teams/Tickets, which have flat-named stragglers). |

## G1 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| `IShiftManagementService.GetActiveAsync`/`GetByIdAsync` still return `EventSettings` entity alongside the new clean `IBurnSettingsService` | `Interfaces.Shifts.IShiftManagementService` | In-flight tonight (#809 parallel lane) — once callers migrate to `IBurnSettingsService`, remove the old methods and their baseline rows. | y |
| 5 further entity-leak baseline rows (`Shift`, `Rota`, `VolunteerEventProfile` ×2, `ShiftSignup`) outside #809's scope | `IShiftManagementService`, `IShiftSignupService` | Not covered by tonight's lane. File as a follow-up G1 item once #809 lands — likely the next-highest-value Shifts cleanup. | y |
| `ShiftsController` HUM0031 grandfather | `src/Humans.Web/Controllers/ShiftsController.cs` | Tracked under #857 (Lane 2 tonight). | y |

## G2 queue notes

`VolunteerEventProfile`'s dietary/medical columns are retained-but-unused pending a post-prod-soak drop (already migrated to `Profile`). This is a named demolition-inventory item already tracked in the doc.

## Verdict

**G1: 3 gaps (7 entity-leak baseline rows — 1 in-flight via #809, HUM0031×1 tracked) · G3: 2 gaps (EF-InMemory repo tests ×3, DbContext-backed service tests ×12 — largest G3.2 gap in the batch)**
