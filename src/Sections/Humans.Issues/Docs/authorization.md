# Issues — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `IssuesController` | Class | `[Authorize]` (authenticated) | — |
| `IssuesController` runtime guards | In-method | `authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)` on every mutating endpoint | Resource-based (see handler below) |
| `BackdoorIssuesController` (in `Humans.Backdoor`) | Class | `[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]` (personal-key auth) | `BackdoorApiKeyAuthFilter` (key-authed agent read+write API at `/api/backdoor/issues`; reads and triages this section through `IIssueTriage`) |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `IssuesAuthorizationHandler` | `IssuesOperationRequirement` (`Handle`) | `IssueDetail` | `Authorization/IssuesAuthorizationHandler.cs` (registered in `Section.cs`) |
