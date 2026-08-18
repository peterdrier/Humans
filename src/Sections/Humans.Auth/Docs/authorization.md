# Auth — Authorization

`Humans.Auth` is a horizontal (cross-cutting) section — see `docs/architecture/peters-hard-rules.md`. It owns the role-assignment resource handler consumed by `Humans.Users`' `UsersAdminController.AddRole`/`EndRole`.

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `RoleAssignmentAuthorizationHandler` | `RoleAssignmentOperationRequirement` (`Manage`) | `string` (roleName) | `Authorization/RoleAssignmentAuthorizationHandler.cs` — also backs the named `PolicyNames.RoleAssignmentManage` policy registered in `AuthorizationPolicyExtensions`; call sites can reach it either via the named policy or by passing the requirement directly |
