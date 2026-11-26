# Backdoor MCP server (v0.1)

## Business Context

Scheduled Claude jobs and Claude Code already talk to Humans through the Backdoor REST routes, but each agent has to be told which routes exist and build every call itself. An MCP endpoint lets an agent connect to one address, ask what it may do, and call named tools. v0.1 proves the two ways in and nothing else: an API key (jobs, Claude Code) and "Sign in with Humans" (the Claude apps), plus one `hello` tool that says who you are.

Peter's rules for it (2026-11-26): nobody gets more through MCP than they have in the app; both ways in resolve to the same person, with roles read fresh on every call; tools are refused unless they say who they are for; audit entries name the person, never a service.

## User Stories

**As the owner of an API key,** I add `https://<host>/api/backdoor/mcp` to Claude Code with my key in the `X-Api-Key` header, and `hello` answers with my name, my roles and that I came in with a key.

**As a person some scope admits, using the Claude app,** I add the same address as a custom connector with the client id from my API keys page, a Humans tab opens, I sign in the usual way, press Allow, and `hello` answers with who I am and that I came in through "Sign in with Humans". I stay connected until the connection's 30 days run out, I disconnect, or it is revoked.

**As that person,** I see my Claude connections on the API keys page beside my keys and can revoke any of them. Admin can revoke anyone's.

**Nobody else gets in:**
- No key or token → 401. A browser cookie alone → 401: the endpoint never accepts the cookie, so a page in a signed-in browser cannot drive it.
- A key or token whose owner no scope admits any more (role gone, suspended, expired key) → refused on the next call, even though the client still holds it.
- A tool that declares no scope is neither listed nor callable, for anyone.
- A tool whose scope does not admit the caller is not listed, and calling it by name is refused.

## How it works

### One sign-in scheme for keys

The key check moves out of the MVC filter into an ASP.NET authentication scheme, `BackdoorApiKey`. The handler reads `X-Api-Key`, resolves it through `IBackdoorApiKeyService.ResolveOwnerAsync` (unchanged: hash lookup, active account, some scope admits, not expired, last-used stamped) and returns a principal carrying only the owner's id. The Shell's claims transformation then adds roles and account state, exactly as for a cookie sign-in, so a key principal and a browser principal for the same person are the same shape.

The REST controllers keep `[AllowAnonymous]` and the filter, so the onboarding gates and every route answer as today. The filter now asks the scheme for the principal instead of resolving the key itself, then checks the controller's scope.

### The MCP endpoint

- Official C# SDK, `ModelContextProtocol.AspNetCore`, stateless HTTP transport (nothing held between calls). Mapped at `/api/backdoor/mcp` through Backdoor's `ISectionEndpoints`.
- The endpoint's policy names only the Backdoor schemes (the key, and the sign-in token below), never the cookie, and requires that some scope admits the person. No CORS policy.
- Tools are hand-written classes in Backdoor, each carrying `[BackdoorScope]`, calling the same section interfaces the REST routes call. Two request filters enforce scope on every `tools/list` and `tools/call`: a tool with no scope is hidden and refused, a tool whose scope does not admit the caller is hidden and refused.
- `hello` (scope Admin and Board) returns the caller's id, name, active roles, and which way they came in.

### "Sign in with Humans" (OAuth), second PR

Humans becomes an OAuth authorization server for the Claude apps only, using OpenIddict. Not built yet: how its data is stored waits on Peter (see the end of this section).

- Authorization code with PKCE (S256), and rotating refresh tokens. One public client, the Claude apps (return address `https://claude.ai/api/mcp/auth_callback`), registered in code. No client secrets, no dynamic registration. Claude Code stays on keys.
- The person types that client's id into Claude's connector dialog, so the API keys page shows the id and the steps. (Accepting Anthropic's client metadata address as the id would remove that step; not in v0.1.)
- Tokens are made out to this host's MCP address, from one configured public base URL per environment, and are refused anywhere else, REST routes included.
- Access token one hour; refresh token 30 days, not sliding. Tokens use the app's Data Protection key ring, already kept in the database, so a deploy does not drop every connection.
- Token validation checks the stored connection on every call, so a revoked connection stops at once.
- The token's `sub` becomes the principal's user id, so the Shell's claims transformation adds roles and state exactly as for a key. The token path also refuses any account whose raw state is not Active, so a merged-away id never acts as the survivor.
- A request carrying both a key and a token is refused, so two people's rights never merge.
- The approval page is a Backdoor page behind the normal cookie sign-in, localized, with antiforgery on Allow and Deny. It refuses anyone no scope admits, always asks (never approves silently), names Claude and the return address, and says to allow only a sign-in you started yourself just now. Allow writes an audit entry and sends the person a notification.
- The MCP endpoint answers 401 with the standard pointer (`WWW-Authenticate` naming `/.well-known/oauth-protected-resource`), which is how the Claude app finds the sign-in. The discovery documents are public by necessity, so the endpoint stops being unadvertised.

**Waiting on Peter: where OpenIddict keeps its data.** Its ready-made EF stores read and write the DbContext outside any repository, and every one of its tables carries a concurrency token, both against hard rules. Options: (a) use them anyway and record the exception; (b) give OpenIddict our own stores over a Backdoor repository, so the tables are ours, `backdoor_*` named, with no concurrency tokens; (c) write the OAuth server by hand. Recommended: (b).

### Connections

A connection is one person's authorization for the Claude apps. The API keys page lists the person's connections (client, connected at) beside their keys, with Revoke, which stops it at once and writes an audit entry. Admin sees and revokes everyone's, as with keys. A daily job prunes expired tokens.

GDPR: export lists a person's connections; erasure deletes them; an account merge revokes the eliminated account's connections with an audit entry (the person reconnects), since the tokens carry the old id.

## Data Model

- Keys: unchanged.
- Sign-in (second PR): applications come from code; authorizations and tokens in Backdoor tables, generated by `dotnet ef migrations add`, carrying the person's id as subject.

## Not in v0.1

- Tools beyond `hello`. Each later tool comes with its scope, its page's own rule, a `Backdoor.md` row and a test that the wrong person is refused.
- Marking audit entries as made through Backdoor. `hello` writes nothing; the flag comes with the first write tool.
- Keys or connections narrower than their owner (for example read only).
- A per-key or per-connection rate limit. Calls share the per-IP limit, so the Claude apps share Anthropic's addresses' budget.
- The three Backdoor routes that give Admin and Board more than their pages (Peter, 2026-11-26: later).

## Related

- Research write-up: https://claude.ai/artifact/1KfVAzXaBcntkPwJdMFXNZ
- Routes and gating: [`Backdoor.md`](../Backdoor.md), [`authorization.md`](../authorization.md).
