# Camps — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CampController` | Class | None at class level — anonymous public actions + `[Authorize]` per action | Camp lead + CampsAdmin runtime checks |
| `CampController.Index` / `Details` / `SeasonDetails` | Action | `AllowAnonymous` | Override |
| `CampController.Details` / `SeasonDetails` runtime guard | In-method | A season outside `Active`/`Full` returns 404 unless the viewer passes the `Manage` check (lead of that camp, or CampsAdmin/Admin) — nobodies-collective/Humans#993 | Resource-based (see handler below) |
| `CampController.*` (Contact/Register/Edit/OptIn/Withdraw/HistoricalNames/Images/Members/Roles/etc.) | Action | `[Authorize]` (authenticated) | — |
| `CampController` runtime guards | In-method | `authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` via `HumansCampControllerBase` | Resource-based (see handler below) |
| `CampAdminController` | Class | `CampsAdmin, Board, Admin` | `CampsPolicies.Read`; every POST, the role create/edit forms and `ExportCamps` carry `CampsPolicies.Manage` (`CampsAdmin, Admin`); `Delete` is `AdminOnly` |
| `CampAdminController.Delete` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampComplianceController` | Class | `CampsAdmin, Board, Admin` OR any team/sub-team coordinator (custom handler) | `CampsPolicies.Compliance` (read-only Barrios compliance matrix at `/Barrios/Admin/Compliance`, split from `CampAdminController` so coordinators can view role staffing) |
| `CampApiController` | Class | `AllowAnonymous` (with `BarriosPublic` CORS) | — |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `CampAuthorizationHandler` | `CampOperationRequirement` (`Manage`, `SubmitEvent`) | `CampInfo` / `Camp` entity / camp id (`Guid`) | `Authorization/CampAuthorizationHandler.cs` (registered in `Section.cs`) |

`CampsPolicies.Compliance` is a composite policy (`CampComplianceAccessHandler`, `Authorization/CampComplianceAccessHandler.cs`, registered in this section's `Section.cs` / `Section.cs`) — it short-circuits for CampsAdmin/Board/Admin and otherwise admits any team/sub-team coordinator via `IShiftService.GetCoordinatorTeamIdsAsync`, gating only the read-only Barrios compliance matrix.
