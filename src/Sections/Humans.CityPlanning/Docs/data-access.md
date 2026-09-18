# CityPlanning — Data Access

## City Planning

Project: `src/Sections/Humans.CityPlanning` — services under `Services/`,
repository under `Data/`. **DbContext:** `CityPlanningDbContext`.
`CityPlanningRepository` injects `IDbContextFactory<CityPlanningDbContext>`
directly. Owns `CityPlanningSettings`, `CampPolygons`,
`CampPolygonHistories`.

### CityPlanningService (Scoped)

Repository: `ICityPlanningRepository`.

| Table | R/W |
|-------|-----|
| CityPlanningSettings | R/W |
| CampPolygons | R/W |
| CampPolygonHistories | R/W |

Cross-section calls via `ICampServiceRead`, `ITeamServiceRead`,
`IUserServiceRead`, plus the `IAuditLogService` crosscut for settings
writes. Uses `CityPlanningOptions`. No `IMemoryCache`.

### CityPlanningGdprContributor (Scoped)

No repository, no cache, no dependencies. Implements `IUserDataContributor` —
`ContributeForUserAsync` (Article 15) always returns empty (no by-editor read
path exists); `EraseForUserAsync` (Article 17) is a no-op — the section's only
user-scoped columns are bare attribution FKs, retained per
`Docs/CityPlanning.md`'s GDPR section.

---


