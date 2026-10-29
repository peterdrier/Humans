<!-- freshness:triggers
  src/Sections/Humans.Teams/Controllers/**
  src/Sections/Humans.Teams/Contracts/HumansTeamControllerBase.cs
  src/Sections/Humans.Teams/Authorization/**
  src/Sections/Humans.Teams/Section.cs
-->
<!-- freshness:flag-on-change
  Route/attribute table below is read off the controllers; any auth attribute or resolver change moves a row.
-->

# Teams — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `TeamController` | Class | `[Authorize]` (authenticated) | — |
| `TeamController.Index` | Action | `AllowAnonymous` | Override |
| `TeamController.Details` | Action | `AllowAnonymous` | Override |
| `TeamController.Tree` | Action | `AllowAnonymous` | Override (`/Teams/Tree` department tree; hidden and system teams never listed; anonymous viewers see only public-page departments and get links only to public/promoted teams — `TeamDirectoryBuilder.BuildTree(..., isAuthenticated)`) |
| `TeamController.Summary` | Action | `TeamsAdmin, Board, Admin` | `PolicyNames.TeamsAdminBoardOrAdmin` |
| `TeamController.CreateTeam` (GET/POST) | Action | `TeamsAdmin, Board, Admin` | `PolicyNames.TeamsAdminBoardOrAdmin` |
| `TeamController.EditTeam` (GET/POST) | Action | `TeamsAdmin, Board, Admin` | `PolicyNames.TeamsAdminBoardOrAdmin` |
| `TeamController.DeleteTeam` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `TeamController.GetTeamGoogleResources` | Action | `TeamsAdmin, Board, Admin` | `PolicyNames.TeamsAdminBoardOrAdmin` |
| `TeamController.EditTeam` (POST) runtime guard | In-method | `authorizationService.AuthorizeAsync(User, PolicyNames.AdminOnly)` — non-Admin editors post no `IsSensitive` value (checkbox is `authorize-policy="AdminOnly"`-suppressed), so the flag is passed as leave-unchanged unless the editor is a global Admin. View-model hygiene, not the gate: it keeps a suppressed checkbox from reading as "set it to false" | `PolicyNames.AdminOnly` |
| `TeamService.UpdateTeamAsync` `IsSensitive` write | In-method | `adminAuthorization.RequireCurrentUserIsAdminAsync()` when the call changes the flag — the enforcement point for the invariant, so no caller can set `IsSensitive` around Edit Team. A call passing the flag's current value is not a write and is not gated | `RoleNames.Admin` via `IAdminAuthorizationService` |
| `TeamMessageController` | Class | `[Authorize]` (authenticated); `GET`/`POST /Teams/{teamId}/SendMessage/{id}`. In-method: the team must be in `ITeamMessageOptionsProvider`'s list for the viewer (coordinator of an active team with a linked, synced group) or 403 — Admin and TeamsAdmin get no bypass; an opted-out recipient or self-send redirects to the profile. POST carries an anti-forgery token. | `[Authorize]` on the class, `[ValidateAntiForgeryToken]` on the POST |
| `TeamMessageButtonsViewComponent` | `Profile` user part | Any authenticated viewer on another member's profile; renders nothing on the viewer's own profile, for an opted-out recipient, or when the viewer may send as no team. `TeamMessageController` re-checks. | — |
| `TeamAdminController` | Class | `[Authorize]` (authenticated) | Coordinator checks at runtime via `HumansTeamControllerBase` |
| `TeamAdminController.Roles` / `EditRole` / `ToggleManagement` | In-method | `TeamsAdmin, Admin` | `AuthorizationService.AuthorizeAsync(User, PolicyNames.TeamsAdminOrAdmin)` — drives the management toggle on the role form; `ToggleManagement` forbids everyone else (coordinators reach the page through `ResolveTeamManagementAsync` but cannot flip a role's management flag) |
| `TeamAdminController.Roster` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` (narrows the class-level coordinator-reachable `ResolveTeamManagementAsync` down to Board-or-Admin only — coordinators don't see the full-name roster) |
| `TeamAdminController` runtime guards (most actions) | In-method | `authorizationService.AuthorizeAsync(User, team, TeamOperationRequirement.ManageCoordinators)` via `ResolveTeamManagementAsync` | Resource-based (see handler below) |
| `TeamAdminController.EarlyEntry` / `EarlyEntry/Add` / `EarlyEntry/Edit` / `EarlyEntry/Remove` / `EarlyEntry/LookupTicket` | In-method | `authorizationService.AuthorizeAsync(User, team, TeamOperationRequirement.ManageEarlyEntry)` via `ResolveEarlyEntryManagementAsync` (Admin/TeamsAdmin/Board any team; EarlyEntryAdmin any team; coordinator own team) | Resource-based (see handler below) |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `TeamAuthorizationHandler` | `TeamOperationRequirement` (`ManageCoordinators`, `ManageEarlyEntry`) | `TeamInfo` | `Authorization/TeamAuthorizationHandler.cs` (registered in `Section.cs`) — Admin/TeamsAdmin/Board: any team, any op; `EarlyEntryAdmin`: any team for `ManageEarlyEntry` only; team coordinator: own team only (both ops) |
