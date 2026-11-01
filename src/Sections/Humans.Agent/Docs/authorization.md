# Agent — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `AgentController` | Class | `[Authorize]` (authenticated) | — |
| `AgentController.Ask` | In-method | `auth.AuthorizeAsync(User, user.Id, [new AgentRateLimitRequirement()])` (requirement instantiated directly — not a registered named policy) | Resource-based (see handler below) |
| `AgentController.Conversations` / `ConversationDetail` | In-method | `RoleChecks.IsAdminOrBoard(User)` widens the list and detail views from the viewer's own conversations to every user's, with owner names resolved | `RoleChecks.IsAdminOrBoard` |
| `AdminAgentController` (`/Agent/Admin`) | Class | `Board, Admin` | `PolicyNames.BoardOrAdmin` (`Index`, `Status`, `Conversations/{id}/Prompt` inherit) |
| `AdminAgentController.Settings` (POST) / `ReloadKnowledgeBase` | Action | `Admin` | `PolicyNames.AdminOnly` (the `/Settings#agent` tab is gated `AdminOnly` by `ISectionSettings`) |
| `BackdoorAgentController` (in `Humans.Backdoor`) | Class | `[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]` (personal-key auth) | `BackdoorApiKeyAuthFilter` (key-authed agent transcript read API at `/api/backdoor/agent` — `List`, `Get`, `GetMessages`; reads this section through `IAgentTranscriptRead`) |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `AgentRateLimitHandler` | `AgentRateLimitRequirement` | `Guid` (user id) | `Authorization/AgentRateLimitHandler.cs` — the requirement is instantiated directly at its one call site instead of resolved via a `PolicyNames` constant, so it doesn't appear in the canonical policy table |
