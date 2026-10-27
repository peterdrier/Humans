# GoogleIntegration — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `GoogleController` | Class | `[Route("Google")]` only — no class-level `[Authorize]` | — |
| `GoogleController.Resource` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `GoogleController.Human` | Action | `UsersAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `GoogleController.UpdateSyncSetting` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.SyncSystemTeams` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.SyncResults` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.ActivityLog` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.CheckGroupSettings` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.GroupSettingsResults` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.RemediateGroupSettings` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.RemediateAllGroupSettings` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.AllGroups` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.LinkGroupToTeam` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.Sync` | Action | `TeamsAdmin, Board, Admin` | `PolicyNames.TeamsAdminBoardOrAdmin` |
| `GoogleController.SyncPreview` | Action | `TeamsAdmin, Board, Admin` | `PolicyNames.TeamsAdminBoardOrAdmin` |
| `GoogleController.SyncExecute` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.SyncExecuteAll` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.ProvisionEmail` | Action | `UsersAdmin, Admin` | `PolicyNames.HumanAdminOrAdmin` |
| `GoogleController.Accounts` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.ProvisionAccount` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.SuspendAccount` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.ReactivateAccount` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.ResetPassword` / `ResetPasswordAndGenerate2Fa` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.LinkAccount` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.SyncOutbox` / `RequeueOutboxEvent` / `RequeueAllFailedOutboxEvents` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.RerunGoogleSync` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.CheckEmailRenames` / `EmailRenames` / `EmailFlagViolations` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.Index` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleSyncHistoryMigrationAdminController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `WorkspaceIdentityController` (`[Route("Google")]`) | Class | authenticated | `[Authorize]` |
| `WorkspaceIdentityController.Set` (`WorkspaceEmail`) | Action | the signed-in member, on their own rows only | class `[Authorize]`; target is the current user |
| `WorkspaceIdentityController.AdminSet` (`Admin/{id:guid}/WorkspaceEmail`) | Action | `UsersAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `TeamResourcesController` (`[Route("Google/Teams/{slug}/Resources")]`) | Class | authenticated | `[Authorize]` |
| `TeamResourcesController` (the six POST actions) | Action | authenticated | `[Authorize]` on each action |
| `TeamResourcesController` (every action) | Resource | `TeamsAdmin, Board, Admin` on any team, else `ITeamResourceService.CanManageTeamResourcesAsync` on the team's department (a sub-team checks its parent) | `TeamResourceAuthorizationHandler` via `IAuthorizationService.AuthorizeAsync(User, team, TeamResourceManageRequirement.Instance)`; unknown slug → 404; denied → 403; a `{resourceId}` from another team → 404, nothing dispatched |
| `WorkspaceIdentityCardViewComponent` (admin detail user part) | Render | admin view only (host page admits UsersAdmin, Board, Admin); Re-run button `Admin` | `ProfileCardViewMode.Admin` + `PolicyNames.AdminOnly` |
