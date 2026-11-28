<!-- freshness:triggers
  src/Sections/Humans.Backdoor/**
  src/Humans.Web/Authorization/MembershipRequiredFilter.cs
  src/Humans.Web/Authorization/NameRequiredFilter.cs
  src/Sections/Humans.Agent/Contracts/IAgentTranscriptRead.cs
  src/Sections/Humans.Issues/Contracts/IIssueTriage.cs
  src/Sections/Humans.Notifications.Contracts/INotificationInboxRead.cs
  src/Sections/Humans.Surveys/Contracts/ISurveyAnalysisRead.cs
  src/Sections/Humans.Store/Contracts/IStoreAccountingRead.cs
  src/Sections/Humans.Expenses/Contracts/IExpenseReportService.cs
  src/Sections/Humans.Tasks/Contracts/ITaskWrite.cs
  src/Sections/Humans.Finance.Contracts/IFinanceService.cs
-->
<!-- freshness:flag-on-change
  Re-read the surface table and the auth model whenever a controller, the key service or the auth filter changes: the routes are a published contract for agents, and the "one key = one human" rule is the whole point of the section.
-->

# Backdoor — Section Invariants

Vocabulary: [`CONTEXT.md`](CONTEXT.md).

The Backdoor API. Every key-authed API an agent talks to lives here, under `/api/backdoor/*`, gated by one personal key per human.

## Concepts

- A **Backdoor API key** is a key created for a *person*, not a service. Its plaintext exists only at the moment of creation; the database keeps a SHA-256 hash and a 12-character display prefix.
- **Create / rotate / revoke** are the whole lifecycle. There is no "read the key back" — a lost key is rotated.
- A **scope** (`BackdoorScope`) names who a key-authed controller serves. Every controller declares one with `[BackdoorScope(...)]`. A person may hold a key when any scope admits their active account, and the key reaches only the controllers whose scope admits them. There are two: **Admin and Board** (`AdminAndBoard`: a full Admin or a Board member), declared by every controller except tasks; and **Tasks** (holds `Tasks.Manage` on some team, through `IPermissionService`; TasksAdmin and Admin hold it everywhere), declared by the tasks bulk-load controller. A new scope is an enum value plus its test in `BackdoorApiKeyService.AdmitsAsync`.
- A **connection** is one person's "Sign in with Humans" approval for the Claude apps: the OAuth authorization behind their tokens. Like a key it acts as that person; unlike a key it is made by pressing Allow, not created on the keys page.
- The **Backdoor API** is the key-authed APIs Backdoor owns. Each is a thin orchestrator over another section's public contracts interface; Backdoor owns no domain data beyond its keys.

## Data Model

### BackdoorApiKey

**Table:** `backdoor_api_keys`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | The human the key belongs to; every request authenticates as them |
| KeyHash | string(64) | SHA-256 of the plaintext, lowercase hex. Unique |
| DisplayPrefix | string(16) | First 12 characters of the plaintext, so a human can tell their rows apart |
| Label | string(100) | Free text — what the key is for |
| CreatedAt | Instant | |
| CreatedByUserId | Guid? | Who made it (the owner, or an Admin creating it for them); nulled on their erasure |
| LastUsedAt | Instant? | Stamped on every successful resolve |
| ExpiresAt | Instant? | Null for Admin and Board owners; otherwise 30 days after creation or rotation. A null key lapses at `CreatedAt` + 30 days once its owner is no longer Admin or Board |
| RevokedAt | Instant? | Null means active |
| RevokedByUserId | Guid? | |

**Indexes / constraints:** unique on `KeyHash` (a presented key must resolve to at most one row); non-unique on `UserId`.

**Cross-section FKs:** `UserId`, `CreatedByUserId`, `RevokedByUserId` → `Users.User` (Users), as bare Guids — the Identity tables stay in `UsersDbContext`.

### BackdoorOAuthConnection

**Table:** `backdoor_oauth_connections`. OpenIddict's authorization, stored through `BackdoorOAuthConnectionStore` over `IBackdoorOAuthRepository`.

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | The person who pressed Allow (OpenIddict's subject) |
| ClientId | string | The OAuth client, today always `humans-claude` |
| Status | string | `valid` or `revoked` |
| Type | string | `permanent` for an approval; `ad-hoc` for OpenIddict's own |
| Scopes | string? | Space-separated; only `offline_access` is granted |
| CreatedAt | Instant | |
| Properties | string? | OpenIddict's extra properties, JSON |

**Indexes:** non-unique on `UserId`.

### BackdoorOAuthToken

**Table:** `backdoor_oauth_tokens`. OpenIddict's token entries (authorization codes, access and refresh tokens), stored through `BackdoorOAuthTokenStore`.

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| ConnectionId | Guid? | FK to `backdoor_oauth_connections`, cascade delete |
| ClientId | string? | |
| UserId | Guid? | Subject |
| Type | string | OpenIddict token type |
| Status | string | `valid`, `inactive`, `redeemed`, `rejected` or `revoked` |
| CreatedAt / ExpiresAt / RedeemedAt | Instant? | |
| ReferenceId | string? | Unique; set for reference tokens |
| Payload | string? | The protected token, for reference tokens |
| Properties | string? | JSON |

**Indexes:** unique on `ReferenceId`; non-unique on `UserId`. Neither table carries a concurrency token. OAuth clients are not stored: they come from code (`BackdoorOAuthClients`).

**Cross-section FKs:** `UserId` on both → `Users.User`, as a bare Guid.

## Routing

| Route | Access | Serves |
|-------|--------|--------|
| `/api/backdoor/logs` | read | The in-memory log ring (`InMemoryLogSink`, Base) |
| `/api/backdoor/agent/conversations` | read | Agent conversation transcripts — list, one conversation, its messages; list previews directly select the newest nonempty user message (first on equal timestamps) and retain a 200 UTF-16 code-unit cap without splitting Unicode surrogate pairs — via `IAgentTranscriptRead` |
| `/api/backdoor/issues` | read + write | The issue queue, via `IIssueTriage`; `GET /{id}/screenshot` streams the screenshot (the payload's `ScreenshotUrl`) behind the same reach check |
| `/api/backdoor/surveys` | read | Survey definitions, responses and aggregates, via `ISurveyAnalysisRead` |
| `/api/backdoor/store/order-lines?year=` | read | One row per Store order line of the year — camp and team orders — with effective price, stored snapshot price, VAT, revenue account and invoice number, via `IStoreAccountingRead` |
| `/api/backdoor/store/order-history?year=` | read | One row per audit entry about a Store order of the year (lines added/removed, payments, refunds, deposit returns, invoice, catalog price changes to its products), with actor and description, via `IStoreAccountingRead`; each call is audited (`StoreOrderHistoryExported`) against the key owner, and refused (403) unless the owner can administer the Store (Admin, StoreAdmin, FinanceAdmin) |
| `/api/backdoor/store/payments?year=` | read | One row per `Paid` Store payment of the year (refunds negative) with its Stripe payment intent id, via `IStoreAccountingRead` |
| `/api/backdoor/finance/expense-reports?year=&status=` | read | The key owner's expense-report review queue — everything `/Expenses/Review` would show them, filterable by budget year and status — via `IExpenseReportService.GetReviewQueueAsync` |
| `/api/backdoor/finance/expense-reports/{id}` | read | One report in full, including its lines, gated by the same `View` check `/Expenses` uses. The payee name, masked IBAN and Holded timeline fields are further narrowed to the report's submitter (payment half) or a finance admin (push half); the Holded contact/supplier-account/doc ids are finance-admin only — the same split `/Expenses` shows in the browser; a viewer whose `View` passes on another ground (e.g. a category coordinator) gets none of them |
| `/api/backdoor/finance/expense-reports/{id}/attachments/{attachmentId}` | read | The stored bytes of one attachment, same `View` gate as the owning report |
| `/api/backdoor/finance/creditor-accounts` | read | Every 400000xx creditor account, its bindings (all of them) and the bindings with no account at all, via `IFinanceService.ListCreditorAccountsAsync` |
| `/api/backdoor/finance/creditor-accounts/{num}/ledger` | read | One account's balance and cached journal lines, contact header included, via `GetCreditorLedgerAsync` |
| `/api/backdoor/finance/category-map` | read | Every live `finance_holded_category_maps` row, via `GetCategoryMapAsync` |
| `/api/backdoor/finance/sepa-transfers` | read | Every generated SEPA transfer with its booking state, via `GetSepaTransfersAsync` |
| `/api/backdoor/finance/expense-recon?year=` | read | `/Finance/ExpenseRecon` as JSON — totals, lines, orphans, members with open transfers — via `IFinanceService.GetExpenseReconAsync`. Finance admins only; carries no IBAN field. The controller orders available years newest-first, lines by problem/status/newest date, orphans newest-first, and members warning-first then ordinal name ignoring case |
| `/api/backdoor/finance/holded-sync` | read | Holded's purchase-document sync state (`IHoldedService.GetPurchaseDocumentsAsync`) plus the docs Finance could not attribute (`IFinanceService.GetUnmatchedAsync`); no creditor-binding count |
| `/api/backdoor/tasks/bulk` | write | `POST`: loads a whole set of tasks, or checks it with `dryRun`, via `ITaskWrite.BulkLoadAsync`. The key owner needs `Tasks.Manage` on every team the payload names (403 otherwise, nothing loaded; a team that does not exist gets the same 403). Every problem in the payload comes back at once as 422; nothing is created unless there are none. An unknown JSON field is a 400, so a misspelt `dryRun` never loads for real. Loading a payload twice creates the tasks twice. Shape and rules: Tasks' `Docs/Tasks.md` |
| `/api/backdoor/notifications` | read | The key owner's unread notifications, newest first, and the live meters their roles unlock, via `INotificationInboxRead`. Polling marks nothing read |
| `/api/backdoor/mcp` | MCP | The MCP endpoint (stateless HTTP), signed in with an API key or a "Sign in with Humans" token. Lists and runs only the tools whose scope admits the caller; see [`features/mcp.md`](features/mcp.md). Tools: `hello` (Admin and Board), which returns a greeting, the server's address, environment and version, and the caller's id, name, roles and how they signed in |
| `/Backdoor` | Key page | Anyone a scope admits makes, rotates and revokes their own keys; Admin and Board see every key, and Admin creates, rotates and revokes anyone's. Also shows the MCP address and Claude's client id, and lists connections with Revoke (own; Admin revokes anyone's). Linked only from the admin nav |
| `/Backdoor/OAuth/Authorize` | Approval page | Cookie sign-in. The "Sign in with Humans" page: names Claude and the return address, Allow or Deny with antiforgery. Anyone no scope admits gets 403 |
| `/api/backdoor/oauth/token` | public | OpenIddict's token endpoint: code and refresh grants for the registered client, PKCE required |
| `/.well-known/oauth-protected-resource`, `/.well-known/oauth-authorization-server`, `/.well-known/openid-configuration`, `/.well-known/jwks` | public | Discovery: where the MCP endpoint's sign-in lives |

Authentication is the `X-Api-Key` header on every `/api/backdoor/*` request, checked by the `BackdoorApiKey` authentication scheme (`BackdoorApiKeyHandler`). It is never a default scheme, so a key signs nobody in to the browser pages, and the browser cookie is never accepted under `/api/backdoor/*`. The REST routes have no anonymous endpoint. The MCP endpoint also takes a "Sign in with Humans" bearer token (OpenIddict validation), and the token and discovery endpoints are public by necessity.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Owner of an active key | The controllers whose scope admits them, acting as themselves and scoped to their own roles |
| Anyone a scope admits | Makes, rotates and revokes their own keys at `/Backdoor`. Below Admin and Board, a key expires 30 days after it is made or rotated |
| Board member | All of the above, with keys that never expire. Reads every key at `/Backdoor` (owner, label, prefix, dates); never a key value |
| Admin | All Board capabilities. Additionally: creates a key for any Admin or Board member, and rotates or revokes anyone's key |

## Invariants

- Creditor-account JSON orders each account's bindings and unresolved bindings by creation time ascending. Finance supplies the timestamp metadata; the JSON binding fields remain unchanged.

- The notifications JSON projection orders unread rows newest first and visible meters by priority descending. Notifications supplies the role-filtered data and priority metadata; the JSON meter fields remain label, count and link.
- The Holded-sync JSON projection orders unmatched documents by document date descending, matching Finance's unmatched queue. Finance supplies date metadata without imposing list order; the JSON document fields remain unchanged.

- Direct IBAN masking strips presentation separators, including no-break spaces, tabs and wrapped lines, before retaining the visible prefix and suffix.
- Finance response text redaction recognizes valid human-formatted IBANs even when whitespace or hyphens split the country code and check digits.

- The operator key list renders owner identity through `Human` without profile links. Picker labels and eligibility remain separate from key-row attribution; key status and actions use the stored owner ID.
- API-key timestamps on the operator key page use the shared display formatter in Europe/Madrid time, with date ordering and month names following the UI culture; unused or unbounded timestamps still read "never".

- Surveys response exports pass the authenticated API key owner to the Surveys service for its disclosure audit (survey id and row count); requests without an actor cannot export.
- Surveys Markdown response exports use the owning section’s table escaping, preserving literal backslashes and pipes in prompts, names and answers within their cells.
- Surveys aggregate JSON orders questions by authored page and position, and choice option counts by authored option position, using owner-supplied metadata without adding JSON fields.
- Surveys response timestamps use the shared ISO-8601 UTC formatter; the `since` filter parses with `DateFormattingExtensions.Iso8601InstantPattern`.
- Surveys response exports preserve the owning section's privacy rules: closed Asociado ballots have no identity or submission timestamps and order by response id. A `since` filter cannot match their suppressed timestamps.
- Issues list/detail/comments GETs pass request cancellation to their existing read contracts, including issue display-name reads. Write calls retain their existing cancellation boundaries.

- A key resolves to exactly one human, and that human is installed as the request principal — `ClaimTypes.NameIdentifier` plus one `ClaimTypes.Role` claim per active role assignment — so every write records a real actor and every log line is enriched with them. The Issues queue read consults those role claims to scope its result (see below), and the Notifications read passes the whole principal on so its meters are role-gated the same way the bell is; Agent, Logs, Store, and Surveys reads do not. Finance's routes authorize imperatively: each action calls `IAuthorizationService.AuthorizeAsync` against the installed principal — `FinancePolicies.Manage` for the finance-wide routes (creditor accounts, ledger, category map, SEPA transfers, expense reconciliation, Holded sync), and `PolicyNames.ExpenseReportView` (a named policy Expenses' own `ISectionPolicies` registers, wrapping its internal `ExpenseReportOperationRequirement(View)`/handler) for a single report or attachment — the same check `ExpensesController` uses, not a bespoke Backdoor check (peterdrier/Humans#1838).
- JSON triage writes reject undefined issue categories and issue statuses with HTTP 400 using shared localized validation; defined names and numeric values remain accepted.
- Issue status JSON bodies require an explicit `Status` property. Missing status fails JSON binding with HTTP 400 before triage; explicit numeric zero remains valid. Issue-creation JSON likewise requires an explicit `Category`; omission cannot create a Bug by default. It also requires an explicit `ReporterUserId`: empty and unknown IDs return HTTP 400 before issue creation. The reporter lookup retains request cancellation; the existing mutation and key-owner audit attribution are unchanged.
- Every `/api/backdoor/issues/*` route is fetched as the key's owner — id, roles and admin flag — so a Board-only key lists the Board-only queue, and an issue whose id it happens to hold but whose queue would not list it is a 404 to read, to comment on and to patch. Issues enforces that itself, on the same `IssueSectionRouting.CanHandle` the browser reads, so a key reaches exactly as far as its owner does in the browser.
- Expense list/detail financial ownership uses the resolved key owner's `UserInfo.AllUserIds`,
  so reports retained under archived merge ids keep their submitter-only payee fields and detail
  payment timeline. Missing viewer information grants no submitter fields. View authorization,
  IBAN masking and finance-only push fields retain their existing gates; list rows still load
  no payment timeline for non-finance viewers. Viewer lookups retain request cancellation.
- `/api/backdoor/finance/expense-reports` lists exactly the key owner's `/Expenses/Review` queue (unscoped `GetReviewQueueAsync(ct)` candidates filtered by the owner’s report View authorization and the existing active-year category scope before metadata is projected, narrowed by `year`/`status`). A report or attachment the owner's `View` check refuses is a 403, not a 404 — unlike Issues, a machine caller here is told a resource exists but is denied, since the finance-wide routes already disclose that much.
- The database never holds a plaintext key. `BackdoorApiKeyService` hashes on the way in and compares hashes on the way out.
- A key only works for someone a scope admits **whose account state is `Active`**, and only until its `ExpiresAt` — checked at creation, at rotation, **and on every authentication**. Each controller then checks its own scope: a good key whose owner that scope does not admit is 403, and a controller declaring no scope answers 403 to everyone. A role that expires, is revoked, or is swept by account deletion stops the key working on the next request, and so does suspension, which moves `users.State` while deliberately leaving role assignments standing. The row is refused, not revoked, so restoring the role or lifting the suspension restores the key. The key page shows such a key as **Disabled** and withholds Rotate, since rotation applies the same test. An expired key shows as **Expired**; rotating it is how it is renewed.
- Only the owner or a full Admin creates, rotates or revokes a key. `BackdoorController` enforces it through the `AdminOnly` policy on each POST, not only by hiding buttons; someone else's key reads as missing.
- Create and revoke both write an audit entry naming the key and its owner (`BackdoorApiKeyCreated` / `BackdoorApiKeyRevoked`); a rotation is recorded as a revoke followed by a create. Entries written before the rename carry `BackdoorApiKeyIssued`, still declared so they resolve.
- Every controller here is an orchestrator: it calls another section's contracts interface and formats the result. None of them touch a repository or a `DbContext` other than through `IBackdoorApiKeyRepository`.
- A key-authed principal carries the `BackdoorApiKey` authentication scheme (`BackdoorApiKeyHandler.SchemeName`) and only the owner's id from the handler. The Shell's claims transformation adds roles and account state, as for any sign-in, so a key and a browser session for the same person look the same to every later check. The Shell's onboarding gates (`NameRequiredFilter`, `MembershipRequiredFilter`) skip REST key requests because every key-authed controller is `[AllowAnonymous]`, rather than redirecting a JSON client to an HTML page; the MCP endpoint is not MVC, so the gates never see it.
- The MCP endpoint's policy names only the Backdoor schemes (`BackdoorMcpCaller`: the key scheme when an `X-Api-Key` header is present, else OpenIddict token validation) and requires that some scope admits the caller, read fresh on every request. A tool is listed and callable only when it declares `[BackdoorScope]` and that scope admits the caller (`McpToolScopeGate`); the SDK's own default, which allows any tool without a rule, never applies. Stateless transport: nothing is held between calls.
- A "Sign in with Humans" token is made out to this host's MCP address, worked out per request from the forwarded scheme and host, and the approval page refuses a request for any other resource. The token's `sub` is the person's id and also becomes `ClaimTypes.NameIdentifier`, so the Shell's claims transformation adds roles and state exactly as for a key.
- The approval page always asks; nothing is approved silently, even for a returning person. Allow creates a permanent connection, writes `BackdoorConnectionApproved` and notifies the person.
- Token validation checks the stored token and connection on every call, so a revoked connection stops its access and refresh tokens at once. Access tokens last one hour, refresh tokens 30 days, not sliding.
- Every `PATCH /api/backdoor/issues/{id}/*` answers the same way whichever field moved: `{success:true}`, 404 for a missing item, 422 carrying the service's reason for a rejected change.

- Issues mutations map owner-section results to 404 for missing/inaccessible issues, 422 for rule refusals, and 500 for dependency failures. Comment posting uses an explicit missing result. Exceptions and their diagnostic text never select 404/422 or enter the response body.

## Negative Access Rules

- A caller with no `X-Api-Key` header **cannot** reach any endpoint — 401. A signed-in browser session without a key gets the same 401 at the MCP endpoint.
- A request carrying both an `X-Api-Key` header and an `Authorization` header **cannot** reach the MCP endpoint — 401, so two people's rights never merge.
- A person no scope admits **cannot** approve a connection — 403 from the approval page, and no connection is written.
- A revoked connection's tokens **cannot** reach the MCP endpoint (401) or refresh (400).
- A non-Admin **cannot** revoke someone else's connection; it reads as missing.
- A key owner **cannot** see or run an MCP tool whose scope does not admit them, nor any tool that declares no scope: it is left out of `tools/list` and `tools/call` refuses it.
- A key whose owner lacks `Tasks.Manage` on any one team in a bulk-load payload **cannot** load any of it — 403 naming those teams, and a reference that matches no team is named the same way, so the answer never tells an unknown team from an unmanaged one; the payload is not checked further and nothing is created.
- A caller with an unknown or revoked key **cannot** reach any endpoint — 401, deliberately indistinguishable from the above. There is no "server not configured" status: keys are rows, not environment variables.
- An admin **cannot** recover a key's plaintext after creation, their own included.
- A former Admin or Board member **cannot** keep using a key created while they held the role — 401 from the next request on, once no scope admits them.
- An owner below Admin and Board **cannot** use a key past its expiry — 401 — and **cannot** reach an Admin and Board controller (logs, agent, issues, surveys, store, finance, notifications) — 403.
- A suspended Admin or Board member **cannot** use their key: the Backdoor API is exempt from the onboarding gates the account-status wall runs through, so eligibility is the only thing standing between a suspended account and the API.
- A member no scope admits **cannot** be given a key, however the request is made, and gets 403 from `/Backdoor`.
- A non-Admin **cannot** create, rotate or revoke someone else's key, Board included.
- Backdoor **cannot** read another section's tables. Anything it serves comes from that section's published contracts interface.

## Triggers

- On create: an audit entry (`BackdoorApiKeyCreated`), and the plaintext is surfaced once through TempData to the key page.
- On revoke: an audit entry (`BackdoorApiKeyRevoked`) and `RevokedAt`/`RevokedByUserId` stamped.
- On rotate: revoke of the old key, then creation of a replacement carrying the same owner and label — two audit entries.
- On every successful authentication: `LastUsedAt` stamped on the key. An expired key, or one whose owner is no longer eligible, authenticates nothing and is not stamped.
- On GDPR erasure: the human's keys are hard-deleted, and they are detached as the creator or revoker of anyone else's — the row belongs to its owner, not to the admin who handled it.
- On account merge: the eliminated account's keys, and its actor columns, fold onto the survivor. Its connections are revoked instead, each with an audit entry, since their tokens carry the old id; the person reconnects.
- On connection approval: an audit entry (`BackdoorConnectionApproved`) and a notification to the person (`BackdoorConnectionApproved` source, System category).
- On connection revoke: the connection and its tokens set to revoked, and an audit entry (`BackdoorConnectionRevoked`).
- On GDPR erasure: the person's connections and tokens are deleted.
- Daily at 03:40 (`backdoor-oauth-prune`): spent, expired and orphaned tokens and dead connections older than a day are deleted.

## Cross-Section Dependencies

- **Audit actions:** declared on `BackdoorAuditActions` (`src/Sections/Humans.Backdoor/BackdoorAuditActions.cs`), one `AuditActionDefinition` each, written through `IAuditLogService`: `BackdoorApiKeyCreated`, `BackdoorApiKeyRevoked`, `BackdoorConnectionApproved`, `BackdoorConnectionRevoked`, and the retired `BackdoorApiKeyIssued` (no longer written).
- **Agent**: reads transcripts via `IAgentTranscriptRead` (`Humans.Agent.Contracts`).
- **Issues**: reads and triages via `IIssueTriage` (`Humans.Issues.Contracts`).
- **Surveys**: reads definitions, exports and aggregates via `ISurveyAnalysisRead` (`Humans.Surveys.Contracts`).
- **Store**: reads the accounting export — order lines, settled payments and order history per year — via `IStoreAccountingRead` (`Humans.Store.Contracts`). Any active key may read order lines and payments; order history needs an owner who can administer the Store.
- **Expenses**: reads reports, lines, attachments and the review queue via `IExpenseReportService` (`Humans.Expenses/Contracts`, not the contracts leaf: a section reference, since `IExpenseReportService` itself lives there, not in a separate `.Contracts` project). Gated per route (see Invariants) — peterdrier/Humans#1838.
- **Finance**: reads creditor accounts, ledgers, the category map, SEPA transfers and the expense reconciliation via `IFinanceService` (`Humans.Finance.Contracts`). `FinancePolicies.Manage`-gated — peterdrier/Humans#1838. Expense-reconciliation responses order each member's open transfers by generation time, oldest first, in the response projection.
- **Holded**: `IHoldedService.GetPurchaseDocumentsAsync` for the purchase-document sync state on `holded-sync` (`Humans.Holded`'s `Contracts/` folder, a section reference).
- **Budget**: `IBudgetService.GetYearByIdAsync` for the budget-year label (e.g. "2026") an expense report is booked to, for the year filter/field — peterdrier/Humans#1838.
- **Tasks**: loads a set of tasks via `ITaskWrite` (`Humans.Tasks/Contracts`, a section reference). Before calling it the controller checks `Tasks.Manage` on every team in the payload through `IPermissionService` (`Humans.Permissions`), finding each team by slug or id through `ITeamService` (`Humans.Teams.Contracts`) and passing Tasks the id it checked; Tasks holds every other rule. A refusal is logged at Warning.
- **Notifications**: reads the key owner's unread inbox and meters via `INotificationInboxRead` (the `Humans.Notifications.Contracts` leaf, so Backdoor never references the Notifications section project). Rows are the owner's own; meters follow the owner's roles.
- **Auth**: `ISystemRoleService.GetActiveForUserAsync` supplies the roles for each scope check (`ResolveOwnerAsync` at sign-in, `AdmitsAsync` per route or tool). The principal's role claims come separately from the Shell's claims transformation, so a call reads roles more than once; scope decisions use only the service's own lookups, never the claims. `GetActiveUserIdsInRoleAsync` supplies the admin page's recipient list, narrowed to active accounts so the dropdown never offers someone the service would refuse. Each listed key's usable/refused badge reads `BackdoorKeyRow.IsOwnerEligible`, which `ListAsync` computes with the same eligibility test as authentication.
- **Audit**: `IAuditLogService.LogAsync` for the key and connection lifecycle.
- **Notifications (send)**: `INotificationEmitter.SendAsync` tells a person a connection was approved, through the `BackdoorConnectionApproved` source.
- **Gdpr**: `IUserDataContributor` — `backdoor_api_keys` is user-keyed, so the section owes an Article 15 slice (`BackdoorApiKeyService.BackdoorApiKeys`, hash excluded) and an Article 17 erasure. `backdoor_oauth_connections` likewise: `BackdoorConnectionService` exports the slice `BackdoorConnections` and erases the person's connections.
- **Users**: `IUserService.GetUserInfoAsync` for the account-state half of key eligibility and `GetUserInfosAsync` for display names on the admin page and on the API's issue, agent and finance projections (the finance report detail reads its one submitter through `GetUserInfoAsync`); `IUserMerge` to fold an eliminated account's keys onto the survivor.
- **Base**: `InMemoryLogSink` behind `/api/backdoor/logs`.

No section depends on Backdoor. It is a leaf, and deliberately so — the fan-in would otherwise be a cycle. The Shell references nothing in it.

## Architecture

**Owning services:** `BackdoorApiKeyService`, `BackdoorConnectionService` (with OpenIddict's managers calling the Backdoor stores)
**Owned tables:** `backdoor_api_keys`, `backdoor_oauth_connections`, `backdoor_oauth_tokens`
**Status:** (A) Migrated — created at this shape in nobodies-collective/Humans#1128 (2026-08).

### Cross-section read interface

None. No other section consumes Backdoor; its whole surface is HTTP, and its service interface is section-internal.

| Read interface | Methods | Notes |
|---|---:|---|
| — | — | Not cross-section-consumed |

- `BackdoorApiKeyService` never imports `Microsoft.EntityFrameworkCore`; `IBackdoorApiKeyRepository` (Singleton + `IDbContextFactory`, per *Repository rules* in `design-rules.md`) is the only path to the table.
- Rotation changes the old row's revocation fields and inserts its replacement in one repository `SaveChangesAsync` call. A concurrent or already-revoked old key writes neither row, so a failed rotation never strands its owner without a credential.
- **Decorator decision** — no caching decorator. Key lookups are one indexed hash probe per API request at a handful of requests per minute, and a cache would have to be invalidated on every revoke to stay correct about the thing that matters most.
- **Display stitching** — `IUserService.GetUserInfosAsync`.
