# Issues — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `IssuesController` | Class | `[Authorize]` (authenticated) | — |
| `IssuesController` runtime guards | In-method | `authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)` on every mutating endpoint | Resource-based (see handler below) |
| `IssuesApiController` | Class | `[ServiceFilter(typeof(IssuesApiKeyAuthFilter))]` (API-key auth) | `IssuesApiKeyAuthFilter` |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `IssuesAuthorizationHandler` | `IssuesOperationRequirement` (`Handle`) | `IssueDetail` | `Authorization/IssuesAuthorizationHandler.cs` (registered in `Section.cs`) |
