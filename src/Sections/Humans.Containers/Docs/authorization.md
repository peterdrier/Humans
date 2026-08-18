# Containers — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ContainerController` | Class | `[Authorize]` (authenticated) | — |
| `ContainerController` runtime guards | In-method | `authorizationService.AuthorizeAsync(User, target, ContainerOperationRequirement.{Manage, Place})` | Resource-based (see handler below) |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `ContainerAuthorizationHandler` | `ContainerOperationRequirement` (`Manage`, `Place`) | `ContainerAuthorizationTarget` | `Authorization/ContainerAuthorizationHandler.cs` (registered in the section's own `Section.cs` DI) |
