# Cantina — Data Access

## Cantina

Project: `src/Sections/Humans.Cantina`. Owns `cantina_dietary_profiles`
through `CantinaDbContext` (history table `__EFMigrationsHistory_Cantina`).

### DietaryProfileRepository (Scoped)

Sole reader/writer of `cantina_dietary_profiles` (`DietaryProfile`).
Called only by `DietaryProfileService`.

### DietaryProfileService (Scoped)

Implements `ICantinaService` (public reads: `GetDietaryProfileAsync`,
`GetDietaryProfilesAsync`), `IDietaryProfileManagement` (internal: save,
legacy copy), `IUserDataContributor` (export slice `DietaryProfile`,
erase deletes the row) and `IUserMerge` (survivor wins). Reads resolve the
merge chain and the legacy `ProfileInfo` food fields through
`IUserService`. No cache.

### CantinaRosterService (Scoped)

No repository. Cross-section reads via `IShiftManagementService`
(on-site cohort per day, via `GetOnSiteUserIdsForDayAsync`), `ISettingsService`
(active-burn metadata — `GetActiveAsync` returning `EventSettingsInfo`, for
the gate-opening/strike-end date range and event timezone),
`IUserService` (cached `UserInfo` for burner names) and `ICantinaService`
(dietary preference, allergies, intolerances), plus `IAuditLogService`
(roster-export audit). Implements `ICantinaRosterService`.
No direct DB access, no cache.

`MedicalConditions` is intentionally never read here — the cantina plans
around food, not medical history.

Arrival-day feeding: each human is also fed the day before their first
confirmed shift of the event. The first-shift scan reads confirmed
signups through the same `IShiftManagementService` surface, one call
per day from build start through `min(StrikeEndOffset, the last day the
caller needs)` — not the whole event every time.

---
