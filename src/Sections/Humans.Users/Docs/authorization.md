# Users — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ProfileController` (own profile), `ProfileEmailsController` (email grids), `ProfileViewController` (others' profiles, picture, popovers, messaging, search) — all `[Route("Profile")]` | Class | `[Authorize]` (authenticated) | — |
| `ProfileEmailsController.VerifyEmail` (`Me/Emails/Verify`) | Action | `AllowAnonymous` | Override |
| `ProfileViewController.Picture` | Action | `AllowAnonymous` | Override |
| `ProfileViewController.PublicPopover` | Action | `AllowAnonymous` | Override (`[HttpGet("{id:guid}/PublicPopover")]`; 404s unless target is a coordinator on a public-page team) |
| `ProfileEmailsController.AdminAddVerifiedEmail` (`{id:guid}/Admin/Emails/AddVerified`) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `ProfileEmailsController.AdminVerifyEmail` (`{id:guid}/Admin/Emails/Verify`) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `UsersAdminController` | Class | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` (class-level — `AdminList`, `Roles`, `AdminDetail`, `AdminOutbox`, `SuspendHuman`, `UnsuspendHuman`, `RejectSignup`, `AddRole` GET/POST, `EndRole` all inherit) |
| `UsersAdminController.RevealIban` | Action | `Admin` | `PolicyNames.AdminOnly` (override; `Audience` and `PurgeHuman` are the other `AdminOnly` overrides) |
| `UsersAdminController.AddRole/EndRole` runtime guards | In-method | `authorizationService.AuthorizeAsync(User, roleName, PolicyNames.RoleAssignmentManage)` — called via the named policy string rather than passing `RoleAssignmentOperationRequirement.Manage` directly (still resolves to the same resource-based handler, owned by `Humans.Auth`) | Resource-based |
| `ProfileEmailsController` email-action runtime guards | In-method | `authorizationService.AuthorizeAsync(User, userId, UserEmailOperations.Edit)` — every mutating `Me/Emails/*` and `{id}/Admin/Emails/*` action plus `Me/LinkedAccounts/Unlink` and the `{id}/Admin/Emails` GET, except the `AdminOnly` overrides above | Resource-based (see handler below) |
| `ProfileApiController` | Class | `[Authorize]` (authenticated) | — |
| `ProfileApiController.Search` | Action | `[Authorize]` inherited (`[HttpGet("search")]`) | — (people search; admin bit never set on this endpoint) |
| `ProfileApiController.BurnerNameCount` | Action | `[Authorize]` inherited (`[HttpGet("burner-name-count")]`) | — (excludes the authenticated viewer; self-exclusion uses session identity, not a caller-supplied id) |
| `ProfileApiController.GetByUserId` | Action | `[Authorize]` inherited (`[HttpGet("by-userid/{userId:guid}")]`) | — |
| `UsersAdminController.PurgeHuman` | Action | `Admin` | `PolicyNames.AdminOnly` (override on the class-level `HumanAdminBoardOrAdmin` controller) |
| `UsersAdminController.Audience` | Action | `Admin` | `PolicyNames.AdminOnly` (override on the class-level `HumanAdminBoardOrAdmin` controller) |
| `UsersAdminAccountMergesController` | Class | `Admin` | `PolicyNames.AdminOnly` (account-merge surface at `/Users/Admin/AccountMerges`; `Index`, `Merge`, `MergeRequest`, `Dismiss`, `Close` all inherit) |
| `ProfileAdminController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `ProfileBackfillAdminController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `ProfilePictureMigrationAdminController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `UsersAdminDebugController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `UserController` | Class | `[Authorize]` (authenticated) | — (account-status wall + cancel-deletion landings at `/User`; exempt from `MembershipRequiredFilter` since these ARE the redirect targets — each action self-checks the caller's `UserState`) |
| `UnsubscribeController` | Class | (no class-level `[Authorize]`) | — |
| `GuestAccountController` | Class | `[Authorize]` (authenticated) | — (profileless-account self-service: comms preferences, GDPR erasure) |
| `GuestAccountController.CommunicationPreferences` (GET) / `UpdatePreference` (POST) | Action | `AllowAnonymous` | Override (accepts an unsubscribe token in place of a session; see `EndpointAuthorizationTests` allowlist) |
| `UserNameBackfillAdminController` | Class | `Admin` | `PolicyNames.AdminOnly` (BurnerName/legal-name backfill onto `User`; idempotent, retires once done) |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `UserEmailAuthorizationHandler` | `UserEmailOperationRequirement` (`Edit`) | `Guid` (target user id) | `Authorization/UserEmailAuthorizationHandler.cs` (registered in `Section.cs`) |
| `HumanAdminOnlyHandler` | `HumanAdminOnlyRequirement` | none (role check: HumanAdmin but not Admin/Board) | `Authorization/HumanAdminOnlyHandler.cs` (registered in `Section.cs`; backs `PolicyNames.HumanAdminOnly`, which has no `[Authorize(Policy=...)]` call site yet) |
