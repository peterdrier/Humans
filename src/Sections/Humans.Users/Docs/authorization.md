# Users — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ProfileController` (own profile), `ProfileEmailsController` (email grids), `ProfileViewController` (others' profiles, picture, popovers, search) — all `[Route("Profile")]` | Class | `[Authorize]` (authenticated) | — |
| `ProfileEmailsController.VerifyEmail` (`Me/Emails/Verify`) | Action | `AllowAnonymous` | Override |
| `ProfileViewController.Picture` | Action | `AllowAnonymous` | Override |
| `ProfileViewController.PublicPopover` | Action | `AllowAnonymous` | Override (`[HttpGet("{id:guid}/PublicPopover")]`; 404s unless target is a coordinator on a public-page team) |
| `ProfileEmailsController.AdminAddVerifiedEmail` (`{id:guid}/Admin/Emails/AddVerified`) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `ProfileEmailsController.AdminVerifyEmail` (`{id:guid}/Admin/Emails/Verify`) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `UsersAdminController` | Class | `UsersAdmin, Board, Admin` | `UsersPolicies.Read` (class-level — `AdminList`, `AdminDetail` inherit); `SuspendHuman`, `UnsuspendHuman`, `RejectSignup` carry `UsersPolicies.Manage` (`UsersAdmin, Admin`) |
| `ProfileEmailsController` email-action runtime guards | In-method | `authorizationService.AuthorizeAsync(User, userId, UserEmailOperations.Edit)` — every mutating `Me/Emails/*` and `{id}/Admin/Emails/*` action plus `Me/LinkedAccounts/Unlink`, except the `AdminOnly` overrides above. The `{id}/Admin/Emails` GET checks `UserEmailOperations.View`, which also admits Board; the view hides every edit control unless the viewer passes `UsersPolicies.Manage`. The Google column's `/Google/WorkspaceEmail` and `/Google/Admin/{id}/WorkspaceEmail` are GoogleIntegration's `WorkspaceIdentityController` (Users decomposition commit 15; see that section's authorization.md) | Resource-based (see handler below) |
| `ProfileApiController` | Class | `[Authorize]` (authenticated) | — |
| `ProfileApiController.Search` | Action | `[Authorize]` inherited (`[HttpGet("search")]`) | — (people search; admin bit never set on this endpoint) |
| `ProfileApiController.BurnerNameCount` | Action | `[Authorize]` inherited (`[HttpGet("burner-name-count")]`) | — (excludes the authenticated viewer; self-exclusion uses session identity, not a caller-supplied id) |
| `ProfileApiController.GetByUserId` | Action | `[Authorize]` inherited (`[HttpGet("by-userid/{userId:guid}")]`) | — |
| `UsersAdminController.PurgeHuman` | Action | `Admin` | `PolicyNames.AdminOnly` (override on the class-level `UsersPolicies.Read` controller) |
| `UsersAdminController.Audience` | Action | `Admin` | `PolicyNames.AdminOnly` (override on the class-level `UsersPolicies.Read` controller) |
| `UsersAdminAccountMergesController` | Class | `Admin` | `PolicyNames.AdminOnly` (account-merge surface at `/Users/Admin/AccountMerges`; `Index`, `Merge`, `MergeRequest`, `Dismiss`, `Close` all inherit) |
| `ProfileAdminController` | Class | `Admin` | `PolicyNames.AdminOnly` |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `UserEmailAuthorizationHandler` | `UserEmailOperationRequirement` (`View`: self, UsersAdmin, Admin, Board; `Edit`: self, UsersAdmin, Admin) | `Guid` (target user id) | `Authorization/UserEmailAuthorizationHandler.cs` (registered in `Section.cs`) |
| `HumanAdminOnlyHandler` | `HumanAdminOnlyRequirement` | none (role check: UsersAdmin but not Admin/Board) | `Authorization/HumanAdminOnlyHandler.cs` (registered in `Section.cs`; backs `UsersPolicies.UsersAdminOnly`, which has no `[Authorize(Policy=...)]` call site yet) |
