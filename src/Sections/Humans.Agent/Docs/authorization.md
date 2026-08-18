# Agent — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `AgentController` | Class | `[Authorize]` (authenticated) | — |
| `AgentController.Ask` | In-method | `auth.AuthorizeAsync(User, user.Id, [new AgentRateLimitRequirement()])` (requirement instantiated directly — not a registered named policy) | Resource-based (see handler below) |
| `AgentApiController` | Class | `[ServiceFilter(typeof(AgentApiKeyAuthFilter))]` (API-key auth) | `AgentApiKeyAuthFilter` |
| `AdminAgentController` (`/Agent/Admin`) | Class | `Admin` | `PolicyNames.AdminOnly` (`Index`, `Status`, `Settings` GET/POST, `ReloadKnowledgeBase`, `Conversations/{id}/Prompt` all inherit) |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `AgentRateLimitHandler` | `AgentRateLimitRequirement` | `Guid` (user id) | `Authorization/AgentRateLimitHandler.cs` — the requirement is instantiated directly at its one call site instead of resolved via a `PolicyNames` constant, so it doesn't appear in the canonical policy table |
