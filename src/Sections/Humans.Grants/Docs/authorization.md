# Grants — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `GrantsAdminController` | Class | Admin, Grants Admin, Board, or a manager of at least one program | `GrantProgramsPolicy.Name` (section-local policy, `GrantsPolicyHandler`) — `/Grants/Admin` |
| `GrantsAdminController.Index` | In-method | `GrantsPolicies.Read` (Admin, Grants Admin, Board) lists every program, others only the programs they manage; `GrantsPolicies.Manage` drives `CanAdminister` | Policy check |
| `GrantsAdminController.New` (GET/POST) | Action | Admin, Grants Admin — managers and Board cannot create programs | `GrantsPolicies.Manage` |
| `GrantsAdminController.Program` | In-method | `ProgramOperationRequirement.View`; `Manage` and `Administer` drive `CanManage` and `CanAdminister`, so Board sees no forms | Resource-based |
| `GrantsAdminController.Edit` (GET/POST) | Action + in-method | Admin, Grants Admin; then `AuthorizeAsync(User, program, ProgramOperationRequirement.Administer)` | `GrantsPolicies.Manage` + resource-based |
| `GrantsAdminController.Issue` / `Revoke` / `Resend` | Action + in-method | The class gate, then `ProgramOperationRequirement.Manage` via `ManageAsync` (Admin, Grants Admin, or a manager of this program; never Board); a grant route whose grant is not in that program returns 404 | `GrantProgramsPolicy.Name` + resource-based |

Every data-modifying action names its own policy; `GrantsAdminAuthorizationTests` pins them. `GrantsPolicies` (Read, Manage) and `GrantsRoles` live in `Contracts/`; the section-local program-pages policy lives in `Authorization/GrantProgramsPolicy.cs`. All three are registered in `Section.cs`.

## View components

| Component | Slot | Who sees it |
|---|---|---|
| `MyGrantsViewComponent` | `UserPartSlots.Profile` | Only the signed-in person, on their own profile (`ProfileCardViewMode.Self` and viewer id = target id) |
| `UserGrantsViewComponent` | `UserPartSlots.AdminDetail` | `UsersPolicies.Read`, admin view mode only; a program links to its page when the viewer passes `ProgramOperationRequirement.View` |

## Policy Handler

| Handler | Requirement | Policy | Path |
|---|---|---|---|
| `GrantsPolicyHandler` | `ManagesGrantsRequirement` | `GrantProgramsPolicy.Name` — Admin/Grants Admin/Board short-circuit, else `IGrantManagement.ManagesAnyProgramAsync` | `Authorization/GrantsRequirements.cs` |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `ProgramAuthorizationHandler` | `ProgramOperationRequirement` (`View` — the program page: Admin/Grants Admin/Board or a manager of this program; `Manage` — issue, revoke, resend: Admin/Grants Admin or a manager of this program; `Administer` — edit the program and its managers: Admin/Grants Admin) | `ProgramInfo` | `Authorization/ProgramOperationRequirement.cs` |
