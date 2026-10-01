# CityPlanning — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CityPlanningController` | Class | `[Authorize]` (authenticated) | — |
| `CityPlanningController` runtime guards | In-method | Map-admin gate = `AuthorizeAsync(User, PolicyNames.CityPlanningMapAdmin)`; plus lead-of-camp and container-placement-phase checks | `CityPlanningMapAdmin` policy + `CityPlanningService` |
| `CityPlanningApiController` | Class | `[Authorize]` (authenticated) | — |
| `CityPlanningApiController` runtime guards | In-method | Same map-admin gate (restore and polygon export); `CanUserEditAsync` for polygon save; `authorizationService.AuthorizeAsync(ContainerOperationRequirement.Place)` on the three container-placement endpoints (save, notes, clear) | `CityPlanningMapAdmin` policy + `RoleChecks.IsCampAdmin` (polygon save) + `CityPlanningService` + resource-based (`ContainerAuthorizationHandler`) |

## Policy Handler

| Handler | Requirement | Policy | Path |
|---|---|---|---|
| `CityPlanningMapAdminHandler` | `CityPlanningMapAdminRequirement` | `PolicyNames.CityPlanningMapAdmin` — succeeds for `Admin` or `CampAdmin`, otherwise for city-planning team members (`ICityPlanningServiceRead.IsCityPlanningTeamMemberAsync`). Gates the `/Settings#city-planning` tab and is the map-admin gate both controllers ask in-method. | `Authorization/CityPlanningMapAdminHandler.cs` (policy registered in `SectionPolicies.cs`, handler in `Section.cs`) |
