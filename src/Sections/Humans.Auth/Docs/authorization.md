# Auth — Authorization

`Humans.Auth` is a horizontal (cross-cutting) section — see `docs/architecture/peters-hard-rules.md`. It owns the role-assignment resource handler consumed by its own `RoleAssignmentsController` (`AddRole`/`EndRole`), and serves the role-assignment admin pages at their stable `/Users/Admin/...` URLs.

## Controllers

| Controller / action | Level | Roles | Policy |
|---|---|---|---|
| `RoleAssignmentsController` | Class | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` (`Roles`, `AddRole` GET/POST, `EndRole` all inherit) |
| `RoleAssignmentsController.AddRole` (POST) / `EndRole` runtime guards | In-method | per target role name | `authorizationService.AuthorizeAsync(User, roleName, PolicyNames.RoleAssignmentManage)` — resource-based, evaluated by `RoleAssignmentAuthorizationHandler`. `AddRole` answers `Forbid`, `EndRole` answers `404` so a denied actor cannot probe assignment ids |
| `RoleAssignmentsCardViewComponent` | User part (`AdminDetail`) | `HumanAdmin, Board, Admin` | Renders only in `ProfileCardViewMode.Admin` and when the viewer passes `HumanAdminBoardOrAdmin`; nothing otherwise |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `RoleAssignmentAuthorizationHandler` | `RoleAssignmentOperationRequirement` (`Manage`) | `string` (roleName) | `Authorization/RoleAssignmentAuthorizationHandler.cs` — also backs the named `PolicyNames.RoleAssignmentManage` policy registered in `AuthorizationPolicyExtensions`; call sites can reach it either via the named policy or by passing the requirement directly |

## The service-layer exception

`design-rules.md §11` makes services auth-free: controllers call `IAuthorizationService.AuthorizeAsync`, services do not check. `IAdminAuthorizationService.RequireCurrentUserIsAdminAsync` (`Auth.Contracts`, impl `Services/AdminAuthorizationService.cs`) is the documented sole exception — the full-Admin guard in front of destructive delete/reset paths. It throws rather than returning a result, and is cycle-safe: it reads role assignments through the section's own repository and never pulls `IAuthorizationService`.

Current callers (all on destructive/reset paths): `Humans.Shifts`' `ShiftManagementService` and `ShiftSignupService`, `Humans.Users`' `UserService`, `Humans.Teams`' `TeamService`. Adding a caller is a deliberate widening of the exception, not a convenience.
