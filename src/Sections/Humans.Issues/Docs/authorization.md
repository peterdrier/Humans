# Issues — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `IssuesController` | Class | `[Authorize]` (authenticated) | — |
| `IssuesController` runtime guards | In-method | `authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)` on every mutating endpoint | Resource-based (see handler below) |
| `BackdoorIssuesController` (in `Humans.Backdoor`) | Class | `[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]` (personal-key auth) | `BackdoorApiKeyAuthFilter` (key-authed agent API at `/api/backdoor/issues` — list, get, create, comment, status, assignee, section, GitHub link; reaches this section through `IIssueTriage`, passing an `IssueViewer` built from the key owner's own claims, so the service applies the same handle/reporter rule it applies to a browsing session) |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `IssuesAuthorizationHandler` | `IssuesOperationRequirement` (`Handle`) | `IssueDetail` | `Authorization/IssuesAuthorizationHandler.cs` (registered in `Section.cs`) |

The table above covers mutating endpoints. `GET /Issues/{id}` is viewer-scoped too, but the
handler above doesn't gate it — `IssuesService` does: out of the viewer's reach reads as gone,
`GetIssueByIdAsync` returns null, and everything else throws the same "not found" a deleted
issue throws. An id is not an oracle for issues outside the caller's queue.
