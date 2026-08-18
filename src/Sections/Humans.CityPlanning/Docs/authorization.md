# CityPlanning — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CityPlanningController` | Class | `[Authorize]` (authenticated) | — |
| `CityPlanningController` runtime guards | In-method | `RoleChecks.IsCampAdmin(User)` and lead-of-camp checks | RoleChecks helper |
| `CityPlanningApiController` | Class | `[Authorize]` (authenticated) | — |
| `CityPlanningApiController` runtime guards | In-method | `RoleChecks.IsCampAdmin(User)` and lead-of-camp checks; `authorizationService.AuthorizeAsync(...)` on three endpoints (camp-polygon edit, camp-polygon history restore, container placement edit) | RoleChecks helper + resource-based |
