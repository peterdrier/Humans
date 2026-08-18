# Events — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `EventsController` | Class | `[Authorize]` (authenticated) + `[ServiceFilter(typeof(EventsFeatureFilter))]` | — |
| `EventsController` barrio-event runtime guards | In-method | `authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.SubmitEvent)` via `HumansCampControllerBase.ResolveCampEventManagementAsync` | Resource-based (handler owned by `Humans.Camps`) |
| `EventsAdminController` | Class | `EventsAdmin, Admin` | `PolicyNames.EventsAdminOrAdmin` |
| `EventsDashboardController` | Class | `EventsAdmin, Admin` | `PolicyNames.EventsAdminOrAdmin` |
| `EventsExportController` | Class | `EventsAdmin, Admin` | `PolicyNames.EventsAdminOrAdmin` |
| `EventsModerationController` | Class | `EventsAdmin, Admin` | `PolicyNames.EventsAdminOrAdmin` |
| `EventsApiController` | Class | `[ApiController]`, `[EnableCors("EventsApi")]`, `[ServiceFilter(typeof(EventsFeatureFilter))]` — no class-level `[Authorize]` | — |
| `EventsApiController.GetEvents/GetEvent/GetBarrios/GetBarrio/GetCategories` | Action | (anonymous reads) | — |
| `EventsApiController.GetPreferences/UpdatePreferences/GetFavourites/AddFavourite/RemoveFavourite` | Action | `[Authorize]` (authenticated) | — |

`EventsController`'s barrio-event submit/create/edit/update/withdraw actions also gate on owner-or-`RoleChecks.IsEventsAdmin` on Edit/Update endpoints.
