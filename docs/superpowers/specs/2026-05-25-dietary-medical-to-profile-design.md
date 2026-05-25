# Move Dietary + Medical from VolunteerEventProfile to Profile — Design

**Date:** 2026-05-25
**Status:** Draft for review
**Approved direction:** Frank (dietary + medical become person/Profile attributes, surfaced on `UserInfo`; medical rides the cached model with surface-level gating). GDPR sign-off asserted.

## Goal

Relocate the six dietary/medical fields from the Shifts-owned `VolunteerEventProfile` (VEP) onto the Users-owned `Profile`, and surface them on the `ProfileInfo`/`UserInfo` cross-section read-model. Consumers then read dietary/medical through `IUserServiceRead` (cached) and write through `IProfileEditorService`, instead of reaching into the Shifts section. The cantina roster (#780) and the dietary-prompt features (#778, already merged) re-point onto this.

**Why:** Dietary preference and medical conditions are properties of a *person*, not of their shift participation. #778 already surfaces dietary on the Profile Edit page, confirming the direction. Moving the data to `Profile` makes it available via the single cached `UserInfo` read everyone already uses, removes a cross-section reach (the cantina no longer needs Shifts data for this), and eliminates the awkward "shift profile owns your allergies" model.

## Fields moving

From `VolunteerEventProfile` → `Profile`:

| Field | Type | EF persistence |
|-------|------|----------------|
| `DietaryPreference` | `string?` | nvarchar(200) |
| `Allergies` | `List<string>` | JSONB + SequenceEqual value comparer |
| `AllergyOtherText` | `string?` | nvarchar(500) |
| `Intolerances` | `List<string>` | JSONB + SequenceEqual value comparer |
| `IntoleranceOtherText` | `string?` | nvarchar(500) |
| `MedicalConditions` | `string?` | nvarchar(4000) — **GDPR Art. 9** |

`VolunteerEventProfile` **retains** `Skills`, `Quirks`, `Languages` and continues to exist (shift-matching data). Only the six fields above move.

## Data model + migration

1. Add the six columns to `Profile` (Domain entity) with the same shapes. Reuse `ProfileConfiguration` + the existing `ConfigureJsonbList()` helper and `List<string>` value comparer pattern from `VolunteerEventProfileConfiguration`.
2. Single EF migration (generated via `dotnet ef migrations add`, **never hand-edited** per `memory/architecture/no-hand-edited-migrations.md`; runs through the EF migration-review gate, `.claude/agents/ef-migration-reviewer.md`):
   - `Up`: add the six `profiles` columns; **backfill** from `volunteer_event_profiles` by `UserId` (1:1); drop the six columns from `volunteer_event_profiles`.
   - Backfill is in-migration (raw SQL `UPDATE ... FROM` for the scalars + JSONB copy for the lists) so no separate data job is needed. ~500 rows.
   - `Down`: reverse (re-add VEP columns, copy back, drop Profile columns).
3. No retention-policy change: the data moves home, it is not duplicated. Right-to-erasure already cascades on the user; confirm the erasure path nulls the new Profile columns (it operates on Profile, so this is automatic — verify in the anonymization flow).

## Read-model

- `ProfileInfo` (in `UserInfo.cs`) gains all six fields. `UserInfo.Create(...)` maps them from the `Profile` entity it already receives — no new data source, no cross-section read in the assembly path.
- `UserInfo`/`ProfileInfo` are produced by the Users section (`UserService`/`CachingUserService`) from Users-owned tables only — so this stays within section rules (unlike sourcing from VEP, which the new single-repository-per-table analyzer #779 would forbid).

## Medical access control (the critical safety work)

`MedicalConditions` will be present on the cached `ProfileInfo`/`UserInfo`. The gating therefore moves entirely to the **surfaces**: medical is withheld unless the viewer holds the `MedicalDataViewer` policy (`Admin` or `NoInfoAdmin`), exactly as today.

Required work — audit and gate **every** place `UserInfo`/`ProfileInfo` is rendered, projected, or serialized, and ensure `MedicalConditions` is blanked unless `MedicalDataViewer`:
- Profile views (own profile, admin profile, popover, badges `_VolunteerProfileBadges`).
- Volunteer search (`ShiftVolunteerSearchBuilder` already gates via `canViewMedical` — re-point its source to `UserInfo`).
- The DietaryMedical page (owner edits own medical — allowed; rendering others' is gated).
- **Serialization boundaries:** any JSON/API endpoint or agent snapshot that serializes `UserInfo`/`ProfileInfo` (e.g. `/api/*`, agent provider). Medical must be excluded there unless explicitly authorized. Enumerate these during implementation and add a test per boundary.
- **Logs:** ensure no code path logs a full `UserInfo`/`ProfileInfo` (would now include medical).

Design choice to make medical hard to leak by accident: keep `MedicalConditions` as a normal `ProfileInfo` property but add a focused review/test that every serializer either omits it or runs behind the policy. (Considered: a wrapper type that forces a policy token to read medical — rejected as over-engineering for this scale, but noted as the fallback if leaks surface.)

## Consumer re-pointing

Writes → `IProfileEditorService` (Profiles section owns the write):
- `ProfileController.Edit` POST (dietary meal-pref + allergies) — already writes Profile-ish data; extend to the new Profile fields.
- `ProfileController.DietaryMedical` GET/POST — now reads/writes `Profile` (via `IProfileEditorService` / `IUserServiceRead`) instead of `GetOrCreateShiftProfileAsync`/`UpdateShiftProfileAsync`.
- `DietaryMedicalViewModel.FromProfile`/`ApplyTo` — retarget from VEP to Profile.

Reads → `IUserServiceRead` (cached `UserInfo`):
- `ThingsToDoViewComponent` dietary nudge — `DietaryPreference` from `UserInfo`.
- `ShiftsController` dietary signup gate (`RedirectIfDietaryMissing*`, `ComputeSignupsBlockedByMissingDietaryAsync`) — `DietaryPreference` from `UserInfo`.
- `DietaryMissingBannerViewComponent` — same.
- `ShiftVolunteerSearchBuilder` + `ShiftViewModels` search results — `DietaryPreference` (+ gated `MedicalConditions`) from `UserInfo`.
- `ShiftSignupService` signup-details assembly (reads all 6 from VEP) — re-point to `UserInfo`.
- **Cantina (#780):** `CantinaRosterService` reads dietary from `UserInfo.GetUserInfosAsync` directly; the `IShiftManagementService.GetOnSite*` + `OnSiteDietaryProfile` additions from #780 are **dropped** (cohort still comes from Shifts signups, but the *dietary* comes from `UserInfo`). Re-evaluate whether the on-site cohort read still needs a Shifts service method or can be expressed differently.

`IShiftManagementService.GetShiftProfileAsync(includeMedical)` — after the move it no longer carries dietary/medical. Decide: keep it for VEP's remaining fields (Skills/Quirks/Languages) and drop the `includeMedical` param, or remove if those have another accessor. The `GetShiftProfileAsync` medical-strip tests move to the new gated Profile read.

## Section docs to update

- `docs/sections/Profiles.md` — now owns dietary/medical.
- `docs/sections/Shifts.md` — VEP no longer owns dietary/medical.
- `docs/sections/Cantina.md` + `docs/features/cantina/daily-roster.md` — read via `UserInfo`.
- `docs/features/profiles/dietary-medical-nudge.md` — data home changed.

## Testing

- Migration round-trips (backfill correctness): a test that seeds VEP-era data and asserts Profile columns after `Up` (or an integration check).
- `ProfileInfo` carries the six fields; `UserInfo.Create` maps them.
- **Medical gating per surface** — one test per render/serialize boundary asserting medical is blanked without `MedicalDataViewer` and present with it.
- Re-point the existing #778 tests (ProfileControllerEdit, DietaryMedicalReplay, ShiftsControllerDietaryGate, ThingsToDo, DietaryMissingBanner, ShiftManagementService strip tests) to the new source.
- Cantina tests read dietary from `UserInfo`.

## Sequencing / impact

- This is the **prerequisite** for cantina #780; #780 pauses and re-points onto `UserInfo` once this lands (much smaller afterward).
- ~60 files. Stage: (1) Domain + EF + migration + backfill; (2) read-model; (3) write path; (4) read path + medical gating audit; (5) cantina re-point; (6) docs. Build + test green at each stage.

## Out of scope

- Changing the dietary option sets / UI copy.
- Any change to VEP's Skills/Quirks/Languages.
- Consent-tracking changes (medical consent, if any, is unchanged by relocation — verify, don't redesign).

## Resolved decisions (Frank, 2026-05-25)

1. **`GetShiftProfileAsync`:** keep it for VEP's remaining fields (Skills/Quirks/Languages); **drop the `includeMedical` parameter** (no longer meaningful once medical lives on Profile).
2. **Cantina on-site cohort:** keep a Shifts service read for "who's on site for day N" (`IShiftManagementService.GetOnSiteUserIdsForDayAsync` stays). Drop `GetOnSiteVolunteerProfilesForDayAsync` + `OnSiteDietaryProfile` — dietary now comes from `UserInfo`.
3. **Right-to-erasure:** assume covered — the anonymization flow operates on `Profile`, so the new columns are handled automatically. No separate erasure wiring.
