# Teams — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `TeamController` | Class | `[Authorize]` (authenticated) | — |
| `TeamController.Index` | Action | `AllowAnonymous` | Override |
| `TeamController.Details` | Action | `AllowAnonymous` | Override |
| `TeamController.Summary` | Action | `TeamsAdmin, Board, Admin` | `PolicyNames.TeamsAdminBoardOrAdmin` |
| `TeamController.CreateTeam` (GET/POST) | Action | `TeamsAdmin, Board, Admin` | `PolicyNames.TeamsAdminBoardOrAdmin` |
| `TeamController.EditTeam` (GET/POST) | Action | `TeamsAdmin, Board, Admin` | `PolicyNames.TeamsAdminBoardOrAdmin` |
| `TeamController.DeleteTeam` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `TeamController.GetTeamGoogleResources` | Action | `TeamsAdmin, Board, Admin` | `PolicyNames.TeamsAdminBoardOrAdmin` |
| `TeamController.EditTeam` (POST) runtime guard | In-method | `authorizationService.AuthorizeAsync(User, PolicyNames.AdminOnly)` — non-Admin editors post no `IsSensitive` value (checkbox is `authorize-policy="AdminOnly"`-suppressed), so the flag is passed as leave-unchanged unless the editor is a global Admin | `PolicyNames.AdminOnly` |
| `TeamAdminController` | Class | `[Authorize]` (authenticated) | Coordinator checks at runtime via `HumansTeamControllerBase` |
| `TeamAdminController.Roster` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` (narrows the class-level coordinator-reachable `ResolveTeamManagementAsync` down to Board-or-Admin only — coordinators don't see the full-name roster) |
| `TeamAdminController` runtime guards (most actions) | In-method | `authorizationService.AuthorizeAsync(User, team, TeamOperationRequirement.ManageCoordinators)` via `ResolveTeamManagementAsync` | Resource-based (see handler below) |
| `TeamAdminController.EarlyEntry` / `EarlyEntry/Add` / `EarlyEntry/Edit` / `EarlyEntry/Remove` / `EarlyEntry/LookupTicket` | In-method | `authorizationService.AuthorizeAsync(User, team, TeamOperationRequirement.ManageEarlyEntry)` via `ResolveEarlyEntryManagementAsync` (Admin/TeamsAdmin/Board any team; EETeamAdmin any team; coordinator own team) | Resource-based (see handler below) |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `TeamAuthorizationHandler` | `TeamOperationRequirement` (`ManageCoordinators`, `ManageEarlyEntry`) | `TeamInfo` | `Authorization/TeamAuthorizationHandler.cs` (registered in `Section.cs`) — Admin/TeamsAdmin/Board: any team, any op; `EETeamAdmin`: any team for `ManageEarlyEntry` only; team coordinator: own team only (both ops) |
