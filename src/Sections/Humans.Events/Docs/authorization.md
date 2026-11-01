# Events — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `EventsController` | Class | `[Authorize]` (authenticated) + `[ServiceFilter(typeof(EventsFeatureFilter))]` | — |
| `EventsController` barrio-event runtime guards | In-method | `authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.SubmitEvent)` via `HumansCampControllerBase.ResolveCampEventManagementAsync` | Resource-based (handler owned by `Humans.Camps`) |
| `EventsAdminController` | Class | `EventsAdmin, Board, Admin` | `EventsPolicies.Read`; settings save, category and venue create/edit/delete/reorder carry `EventsPolicies.Manage` (`EventsAdmin, Admin`) |
| `EventsDashboardController` | Class | `EventsAdmin, Board, Admin` | `EventsPolicies.Read` (read-only; no write actions) |
| `EventsExportController` | Class | `EventsAdmin, Board, Admin` | `EventsPolicies.Read`; the CSV download (`DownloadCsv`) carries `EventsPolicies.Manage` (`EventsAdmin, Admin`) |
| `EventsModerationController` | Class | `EventsAdmin, Board, Admin` | `EventsPolicies.Read`; every POST (`Approve`, `Reject`, `Withdraw`, `RequestEdit`) and the `Edit` form carry `EventsPolicies.Manage` (`EventsAdmin, Admin`) |
| `EventsApiController` | Class | `[ApiController]`, `[EnableCors("EventsApi")]`, `[ServiceFilter(typeof(EventsFeatureFilter))]` — no class-level `[Authorize]` | — |
| `EventsApiController.GetEvents/GetEvent/GetBarrios/GetBarrio/GetCategories` | Action | (anonymous reads) | — |
| `EventsApiController.GetPreferences/UpdatePreferences/GetFavourites/AddFavourite/RemoveFavourite` | Action | `[Authorize]` (authenticated) | — |

Owner-or-`EventsRoles.IsEventsAdmin` applies only to the individual `Submit/{eventId}/Edit` GET/POST; barrio-event actions gate solely through `ResolveCampEventManagementAsync` (Camps' `HumansCampControllerBase`).
