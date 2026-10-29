# Grants — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `GrantsAdminController` | Class | Admin, Board, or a manager of at least one program | `GrantsPolicies.Manage` (section-local policy, `GrantsPolicyHandler`) — `/Grants/Admin` |
| `GrantsAdminController.Index` | In-method | `GrantsPolicyHandler.IsAdministering(User)` (Admin, Board) drives `CanAdminister`; others see only the programs they manage | Role check |
| `GrantsAdminController.New` (GET/POST) | Action | Admin, Board — managers cannot create programs | `PolicyNames.BoardOrAdmin` |
| `GrantsAdminController.Program` | In-method | `AuthorizeAsync(User, program, ProgramOperationRequirement.Manage)` via `AuthorizedAsync` | Resource-based |
| `GrantsAdminController.Edit` (GET/POST) | Action + in-method | Admin, Board; then `AuthorizeAsync(User, program, ProgramOperationRequirement.Administer)` | `PolicyNames.BoardOrAdmin` + resource-based |
| `GrantsAdminController.Issue` / `Revoke` / `Resend` | Action + in-method | Admin, Board, or a program manager; then `ProgramOperationRequirement.Manage` via `ManageAsync`; a grant route whose grant is not in that program returns 404 | `GrantsPolicies.Manage` + resource-based |

Every data-modifying action names its own policy; `GrantsAdminAuthorizationTests` pins them. The section policy name lives in this section (`Authorization/GrantsPolicies.cs`), registered in `Section.cs`.

## View components

| Component | Slot | Who sees it |
|---|---|---|
| `MyGrantsViewComponent` | `UserPartSlots.Profile` | Only the signed-in person, on their own profile (`ProfileCardViewMode.Self` and viewer id = target id) |
| `UserGrantsViewComponent` | `UserPartSlots.AdminDetail` | `PolicyNames.HumanAdminBoardOrAdmin`, admin view mode only |

## Policy Handler

| Handler | Requirement | Policy | Path |
|---|---|---|---|
| `GrantsPolicyHandler` | `ManagesGrantsRequirement` | `GrantsPolicies.Manage` — Admin/Board short-circuit, else `IGrantManagement.ManagesAnyProgramAsync` | `Authorization/GrantsRequirements.cs` |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `ProgramAuthorizationHandler` | `ProgramOperationRequirement` (`Manage` — issue, revoke, resend: Admin/Board or a manager of this program; `Administer` — edit the program and its managers: Admin/Board) | `ProgramInfo` | `Authorization/ProgramOperationRequirement.cs` |
