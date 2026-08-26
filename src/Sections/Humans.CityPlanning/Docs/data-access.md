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

---


