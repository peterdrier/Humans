# CityPlanning — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CityPlanningController` | Class | `[Authorize]` (authenticated) | — |
| `CityPlanningController` runtime guards | In-method | Map-admin gate = `RoleChecks.IsCampAdmin(User)` **or** city-planning team membership; plus lead-of-camp and container-placement-phase checks | `RoleChecks` helper + `CityPlanningService.IsCityPlanningTeamMemberAsync` |
| `CityPlanningApiController` | Class | `[Authorize]` (authenticated) | — |
| `CityPlanningApiController` runtime guards | In-method | Same map-admin gate (restore and polygon export); `CanUserEditAsync` for polygon save; `authorizationService.AuthorizeAsync(ContainerOperationRequirement.Place)` on the three container-placement endpoints (save, notes, clear) | `RoleChecks` helper + `CityPlanningService` + resource-based (`ContainerAuthorizationHandler`) |
