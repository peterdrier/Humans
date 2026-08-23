# Cantina — Data Access

## Cantina

Project: `src/Sections/Humans.Cantina`. Owns no DB tables — orchestrator
only. Dietary data lives on `Profile` and is read through the unified
`UserInfo` read-model.

### CantinaRosterService (Scoped)

No repository. Cross-section reads via `IShiftManagementServiceRead`
(on-site cohort per day, via `GetOnSiteUserIdsForDayAsync`), `IBurnSettingsService`
(active-burn metadata — `GetActiveAsync` returning `BurnSettingsInfo`, for
the gate-opening/strike-end date range and event timezone) and
`IUserServiceRead` (cached `UserInfo` + `ProfileInfo` for dietary
preference, allergies, intolerances). Implements `ICantinaRosterService`.
No direct DB access, no cache.

`MedicalConditions` is intentionally never read here — the cantina plans
around food, not medical history.

Arrival-day feeding: each human is also fed the day before their first
confirmed shift of the event. The first-shift scan reads confirmed
signups through the same `IShiftManagementServiceRead` surface, one call
per day from build start.

---


