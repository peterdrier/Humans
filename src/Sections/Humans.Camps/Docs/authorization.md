# Camps — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CampController` | Class | None at class level — anonymous public actions + `[Authorize]` per action | Camp lead + CampAdmin runtime checks |
| `CampController.Index` / `Details` / `SeasonDetails` | Action | `AllowAnonymous` | Override |
| `CampController.*` (Contact/Register/Edit/OptIn/Withdraw/Rejoin/HistoricalNames/Images/Members/Roles/etc.) | Action | `[Authorize]` (authenticated) | — |
| `CampController` runtime guards | In-method | `authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` via `HumansCampControllerBase` | Resource-based (see handler below) |
| `CampAdminController` | Class | `CampAdmin, Admin` | `PolicyNames.CampAdminOrAdmin` |
| `CampAdminController.Delete` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampComplianceController` | Class | `CampAdmin, Admin` OR any team/sub-team coordinator (custom handler) | `PolicyNames.CampComplianceAccess` (read-only Barrios compliance matrix at `/Barrios/Admin/Compliance`, split from `CampAdminController` so coordinators can view role staffing) |
| `CampApiController` | Class | `AllowAnonymous` (with `BarriosPublic` CORS) | — |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `CampAuthorizationHandler` | `CampOperationRequirement` (`Manage`, `SubmitEvent`) | `CampLookup` / `Camp` entity / camp id (`Guid`) | `Authorization/CampAuthorizationHandler.cs` (registered in `Section.cs`) |

`CampComplianceAccess` is a composite policy (`CampComplianceAccessHandler`, registered in `src/Humans.Web/Authorization/Requirements/`) — it short-circuits for CampAdmin/Admin and otherwise admits any team/sub-team coordinator via `IShiftManagementService.GetCoordinatorTeamIdsAsync`, gating only the read-only Barrios compliance matrix.
