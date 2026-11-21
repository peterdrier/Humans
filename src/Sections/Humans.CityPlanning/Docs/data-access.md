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

Cross-section calls via `ICampService`, `ITeamService`,
`IUserService`, `ISettingsService` (active event), `IContainerService` (container export reads), plus the `IAuditLogService` crosscut for settings
writes. `GetLeadCampsAsync` composes the shared year-scoped leadership lookup from Camps’ read models for this section’s pages, exports and camp placement component. Uses `CityPlanningOptions`. No `IMemoryCache`.

---


