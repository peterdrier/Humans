# Move Dietary + Medical from VolunteerEventProfile to Profile — Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Relocate the six dietary/medical fields from `VolunteerEventProfile` (Shifts) to `Profile` (Users), surface them on `ProfileInfo`/`UserInfo`, and re-point every reader/writer. Medical rides the cached model but is gated at every surface by `MedicalDataViewer`.

**Architecture:** Profiles section owns the columns; reads via `IUserServiceRead` (cached `UserInfo`), writes via `IProfileEditorService`. VEP keeps Skills/Quirks/Languages. One backfilling EF migration.

**Tech Stack:** .NET 10, EF Core (PostgreSQL JSONB), Clean Architecture, xUnit + NSubstitute + AwesomeAssertions, NodaTime.

**Spec:** `docs/superpowers/specs/2026-05-25-dietary-medical-to-profile-design.md`

**Build/test:** `dotnet build Humans.slnx -v quiet -p:NuGetAudit=false` · `dotnet test Humans.slnx -v quiet -p:NuGetAudit=false`. Pre-existing failures unrelated to this work: 2 `StorePayActionTests` (verified failing on `origin/main`).

---

## File Structure

**Domain:** `Profile.cs` (+6 fields), `VolunteerEventProfile.cs` (−6 fields).
**Infrastructure:** `ProfileConfiguration.cs` (+6 column maps), `VolunteerEventProfileConfiguration.cs` (−6), one new migration.
**Application:** `UserInfo.cs` (`ProfileInfo` +6, `Create` mapping), `IProfileEditorService`/`ProfileEditorService` (write the 6), `IShiftManagementService`/`ShiftManagementService` (`GetShiftProfileAsync` loses `includeMedical` + the 6 fields; keep `GetOnSiteUserIdsForDayAsync`), `ShiftSignupService` (re-point read).
**Web:** `ProfileController`, `ThingsToDoViewComponent`, `ShiftsController`, `DietaryMissingBannerViewComponent`, `ShiftVolunteerSearchBuilder`, `DietaryMedicalViewModel`, `ProfileViewModel`, `ShiftViewModels`, the Profile/Shifts views, `_VolunteerProfileBadges`.
**Tests:** migration/backfill, `ProfileInfo` mapping, per-surface medical-gating, re-pointed #778 tests, cantina tests.
**Docs:** `Profiles.md`, `Shifts.md`, `Cantina.md`, `daily-roster.md`, `dietary-medical-nudge.md`.

---

## Stage 1 — Domain + EF + backfilling migration

### Task 1.1: Add the six fields to `Profile`

**Files:** Modify `src/Humans.Domain/Entities/Profile.cs`

- [ ] **Step 1:** Add `DietaryPreference` (`string?`), `Allergies` (`List<string> = []`), `Intolerances` (`List<string> = []`), `AllergyOtherText` (`string?`), `IntoleranceOtherText` (`string?`), `MedicalConditions` (`string?`), copying the XML-doc comments from `VolunteerEventProfile.cs:41-66`. Keep `MedicalConditions`' comment flagging GDPR Art. 9.
- [ ] **Step 2:** Build `src/Humans.Domain` — expected: compiles.

### Task 1.2: EF column mapping on `ProfileConfiguration`

**Files:** Modify `src/Humans.Infrastructure/Data/Configurations/Profiles/ProfileConfiguration.cs`; reference `.../Shifts/VolunteerEventProfileConfiguration.cs:24-39`

- [ ] **Step 1:** Copy the `List<string>` value comparer + `ConfigureJsonbList()` calls for `Allergies`/`Intolerances`, and the `HasMaxLength` for `DietaryPreference`(200)/`AllergyOtherText`(500)/`IntoleranceOtherText`(500)/`MedicalConditions`(4000).
- [ ] **Step 2:** Remove those six maps from `VolunteerEventProfileConfiguration.cs`.
- [ ] **Step 3:** Build `src/Humans.Infrastructure` — expected: compiles.

### Task 1.3: Remove the six fields from `VolunteerEventProfile`

**Files:** Modify `src/Humans.Domain/Entities/VolunteerEventProfile.cs`

- [ ] **Step 1:** Delete the six properties (lines ~41-66), keeping `Skills`/`Quirks`/`Languages`. (This will break consumers — fixed in later stages; expect a red solution build until Stage 3-4. Domain + Infrastructure still build.)

### Task 1.4: Generate the backfilling migration

**Files:** Create migration under `src/Humans.Infrastructure/Migrations/` (generated)

- [ ] **Step 1:** Run `dotnet ef migrations add MoveDietaryMedicalToProfile --project src/Humans.Infrastructure --startup-project src/Humans.Web` (per `memory/architecture/no-hand-edited-migrations.md` — generated, not hand-written).
- [ ] **Step 2:** In the generated `Up`, after the `profiles` `AddColumn`s and before the `volunteer_event_profiles` `DropColumn`s, insert a backfill (`migrationBuilder.Sql(...)`): `UPDATE profiles p SET dietary_preference = v.dietary_preference, allergies = v.allergies, ... FROM volunteer_event_profiles v WHERE v.user_id = p.user_id;` (scalars + JSONB lists; match actual generated column names). Mirror in `Down` (re-add VEP columns, copy back, drop Profile columns). This is the one allowed migration edit — the backfill SQL; document why inline.
- [ ] **Step 3:** Run the EF migration-review gate (`.claude/agents/ef-migration-reviewer.md`). Address findings.
- [ ] **Step 4:** Build the solution — Domain/Infra green; Application/Web red (expected, consumers not yet re-pointed).

---

## Stage 2 — Read-model (`ProfileInfo`/`UserInfo`)

### Task 2.1: Add the six fields to `ProfileInfo` + `UserInfo.Create`

**Files:** Modify `src/Humans.Application/UserInfo.cs`

- [ ] **Step 1 (test):** In `tests/Humans.Application.Tests/`, add a test that `UserInfo.Create(...)` with a `Profile` carrying the six fields produces a `ProfileInfo` exposing them (including `MedicalConditions`). Run — fails to compile (fields absent).
- [ ] **Step 2:** Add the six properties to the `ProfileInfo` record (group with personal attributes; `MedicalConditions` last with a `// GDPR Art. 9 — gate at every surface` comment).
- [ ] **Step 3:** Map them in `UserInfo.Create` from the `profile` argument.
- [ ] **Step 4:** Run the test — passes. Build `src/Humans.Application` — `ProfileService`/`UserService` assembly compiles.

---

## Stage 3 — Write path (Profiles section owns writes)

### Task 3.1: `IProfileEditorService` writes the six fields

**Files:** `src/Humans.Application/Interfaces/Profiles/IProfileEditorService.cs` + impl; inspect existing `SaveProfileAsync`/`ProfileSaveRequest`

- [ ] **Step 1 (test):** Add a test that saving via `IProfileEditorService` persists the six fields onto `Profile`.
- [ ] **Step 2:** Extend `ProfileSaveRequest` (or add a focused method) to carry the six fields, and have the editor write them to `Profile` through `IProfileRepository`. (Reuse the existing save path; avoid new surface if `ProfileSaveRequest` already flows from Edit.)
- [ ] **Step 3:** Run test — passes.

### Task 3.2: Re-point `DietaryMedicalViewModel` + `ProfileController.DietaryMedical`

**Files:** `src/Humans.Web/Models/DietaryMedicalViewModel.cs`, `src/Humans.Web/Controllers/ProfileController.cs` (DietaryMedical GET/POST ~1584-end; Edit GET/POST dietary at ~235-238, ~482-489)

- [ ] **Step 1:** `DietaryMedicalViewModel.FromProfile`/`ApplyTo` retarget from `VolunteerEventProfile` to `Profile`.
- [ ] **Step 2:** DietaryMedical GET reads the owner's `Profile` (via `IUserServiceRead`/`GetCurrentUserInfoAsync`); POST writes via `IProfileEditorService`. Drop `GetOrCreateShiftProfileAsync`/`UpdateShiftProfileAsync` for dietary.
- [ ] **Step 3:** Edit GET/POST dietary fields read/write `Profile` (the meal-pref + allergies path from #778) — now naturally part of the Profile save.
- [ ] **Step 4:** Re-point `ProfileControllerEditTests` + `ProfileControllerDietaryMedicalReplayTests` to the Profile source. Run — green.

---

## Stage 4 — Read path + medical gating (the safety stage)

### Task 4.1: `GetShiftProfileAsync` loses `includeMedical` + the six fields

**Files:** `IShiftManagementService.cs:360`, `ShiftManagementService.cs:1807-1817`; tests `ShiftManagementServiceTests.cs:1116-1160`

- [ ] **Step 1:** Drop the `includeMedical` param; `GetShiftProfileAsync(Guid userId)` returns VEP (Skills/Quirks/Languages only).
- [ ] **Step 2:** Delete the medical-strip tests here; the gating moves to surface tests (Task 4.3). Update callers of the old signature.

### Task 4.2: Re-point dietary readers to `UserInfo`

**Files:** `ThingsToDoViewComponent.cs:123-134`, `ShiftsController.cs:134-178`, `DietaryMissingBannerViewComponent.cs:29-33`, `ShiftSignupService.cs:1104-1109`

- [ ] **Step 1:** Each reads `DietaryPreference` from `UserInfo` (`IUserServiceRead`) instead of `GetShiftProfileAsync`.
- [ ] **Step 2:** Re-point their tests (`ThingsToDoViewComponentDietaryGateTests`, `ShiftsControllerDietaryGateTests`, `DietaryMissingBannerViewComponentTests`). Run — green.

### Task 4.3: Medical gating audit — every render/serialize surface

**Files:** `ShiftVolunteerSearchBuilder.cs:149-155`, `ShiftViewModels.cs:590-594`, `_VolunteerProfileBadges`, Profile views (own/admin/popover), plus an enumerated list of `UserInfo`/`ProfileInfo` serialization points (API endpoints, agent snapshot).

- [ ] **Step 1:** grep every `UserInfo`/`ProfileInfo` consumer and every JSON/serialization boundary. List them in the task notes.
- [ ] **Step 2 (test-first per boundary):** For each surface that can expose medical, add a test: without `MedicalDataViewer`, `MedicalConditions` is blank/absent; with it, present.
- [ ] **Step 3:** Implement the gate at each surface (mirror the existing `canViewMedical` pattern). Ensure no code path logs a whole `UserInfo`/`ProfileInfo`.
- [ ] **Step 4:** Run all surface tests — green. Full solution build green.

---

## Stage 5 — Cantina re-point (#780)

### Task 5.1: Cantina reads dietary from `UserInfo`; drop `OnSiteDietaryProfile`

**Files:** `CantinaRosterService.cs`, `IShiftManagementService` (drop `GetOnSiteVolunteerProfilesForDayAsync` + `OnSiteDietaryProfile`; keep `GetOnSiteUserIdsForDayAsync`), `ShiftManagementService.cs`, `ShiftManagementRepository.cs`/interface, cantina tests.

- [ ] **Step 1:** `CantinaRosterService` gets the on-site cohort via `GetOnSiteUserIdsForDayAsync` and the dietary via `IUserServiceRead.GetUserInfosAsync` (already injected). Remove the `OnSiteDietaryProfile` dependency.
- [ ] **Step 2:** Delete `GetOnSiteVolunteerProfilesForDayAsync` (service + repo) and `OnSiteDietaryProfile`.
- [ ] **Step 3:** Update cantina tests to drive dietary off `UserInfo`. Run — green.

(Note: this work lands on this branch; PR #780 will be closed/superseded or rebased to point here — decide at finish.)

---

## Stage 6 — Docs + final review

### Task 6.1: Update section + feature docs

**Files:** `docs/sections/Profiles.md`, `docs/sections/Shifts.md`, `docs/sections/Cantina.md`, `docs/features/cantina/daily-roster.md`, `docs/features/profiles/dietary-medical-nudge.md`

- [ ] **Step 1:** Profiles now owns dietary/medical; Shifts/VEP no longer does; cantina + nudge read via `UserInfo`; medical gated at surfaces.

### Task 6.2: Full sweep + finish

- [ ] **Step 1:** `dotnet build Humans.slnx` + `dotnet test Humans.slnx` — green except the 2 known `StorePayActionTests`.
- [ ] **Step 2:** Manual smoke: Profile Edit + DietaryMedical round-trip dietary & medical; a non-medical-viewer cannot see others' medical anywhere; cantina roster shows dietary.
- [ ] **Step 3:** Final code review; open PR; reconcile #780.
