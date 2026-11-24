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

The machine surface. Every key-authed API an agent talks to lives here, under `/api/backdoor/*`, gated by one personal key per human.

## Concepts

- A **Backdoor API key** is a credential issued to a *person*, not a service. Its plaintext exists only at the moment of issue; the database keeps a SHA-256 hash and a 12-character display prefix.
- **Issue / rotate / revoke** are the whole lifecycle. There is no "read the key back" — a lost key is rotated.
- A **scope** (`BackdoorScope`) names who a key-authed controller serves. Every controller declares one with `[BackdoorScope(...)]`. A person may hold a key when any scope admits their active account, and the key reaches only the controllers whose scope admits them. There are two: **Staff** (a full Admin or a Board member), declared by every controller except tasks; and **Tasks** (holds `Tasks.Manage` on some team, through `IPermissionService`; TasksAdmin and Admin hold it everywhere), declared by the tasks bulk-load controller. A new scope is an enum value plus its test in `BackdoorApiKeyService.AdmitsAsync`.
- The **machine surface** is the key-authed APIs Backdoor owns. Each is a thin orchestrator over another section's public contracts interface; Backdoor owns no domain data beyond its keys.

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
| CreatedByUserId | Guid? | Who made it (the owner, or an Admin allocating it); nulled on their erasure |
| LastUsedAt | Instant? | Stamped on every successful resolve |
| ExpiresAt | Instant? | Null for Admin and Board holders; otherwise 30 days after issue or rotation. A null key lapses at `CreatedAt` + 30 days once its owner is no longer Admin or Board |
| RevokedAt | Instant? | Null means active |
| RevokedByUserId | Guid? | |

**Indexes / constraints:** unique on `KeyHash` (a presented key must resolve to at most one row); non-unique on `UserId`.

**Cross-section FKs:** `UserId`, `CreatedByUserId`, `RevokedByUserId` → `Users.User` (Users), as bare Guids — the Identity tables stay in `UsersDbContext`.

## Routing

| Route | Access | Serves |
|-------|--------|--------|
| `/api/backdoor/logs` | read | The in-memory log ring (`InMemoryLogSink`, Base) |
| `/api/backdoor/agent/conversations` | read | Agent conversation transcripts — list, one conversation, its messages; list previews directly select the newest nonempty user message (first on equal timestamps) and retain a 200 UTF-16 code-unit cap without splitting Unicode surrogate pairs — via `IAgentTranscriptRead` |
| `/api/backdoor/issues` | read + write | The issue queue, via `IIssueTriage`; `GET /{id}/screenshot` streams the screenshot (the payload's `ScreenshotUrl`) behind the same reach check |
| `/api/backdoor/surveys` | read | Survey definitions, responses and aggregates, via `ISurveyAnalysisRead` |
| `/api/backdoor/store/order-lines?year=` | read | One row per Store order line of the year — camp and team orders — with effective price, stored snapshot price, VAT, revenue account and invoice number, via `IStoreAccountingRead` |
| `/api/backdoor/store/order-history?year=` | read | One row per audit entry about a Store order of the year (lines added/removed, payments, refunds, deposit returns, invoice, catalog price changes to its products), with actor and description, via `IStoreAccountingRead`; each call is audited (`StoreOrderHistoryExported`) against the key owner, and refused (403) unless the holder can administer the Store (Admin, StoreAdmin, FinanceAdmin) |
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
| `/Backdoor` | Key page | Anyone a scope admits makes, rotates and revokes their own keys; Admin and Board see every key, and Admin allocates, rotates and revokes anyone's. Linked only from the admin nav |

Authentication is the `X-Api-Key` header on every `/api/backdoor/*` request. There is no cookie path in and no anonymous endpoint.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Holder of an active key | The controllers whose scope admits them, acting as themselves and scoped to their own roles |
| Anyone a scope admits | Makes, rotates and revokes their own keys at `/Backdoor`. Below Admin and Board, a key expires 30 days after it is made or rotated |
| Board member | All of the above, with keys that never expire. Reads every key at `/Backdoor` (owner, label, prefix, dates); never a key value |
| Admin | All Board capabilities. Additionally: allocates a key to any Admin or Board holder, and rotates or revokes anyone's key |

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
- Every `/api/backdoor/issues/*` route is fetched as the key's owner — id, roles and admin flag — so a Board-only key lists the Board-only queue, and an issue whose id it happens to hold but whose queue would not list it is a 404 to read, to comment on and to patch. Issues enforces that itself, on the same `IssueSectionRouting.CanHandle` the browser reads, so a key reaches exactly as far as its holder does in the browser.
- Expense list/detail financial ownership uses the resolved key owner's `UserInfo.AllUserIds`,
  so reports retained under archived merge ids keep their submitter-only payee fields and detail
  payment timeline. Missing viewer information grants no submitter fields. View authorization,
  IBAN masking and finance-only push fields retain their existing gates; list rows still load
  no payment timeline for non-finance viewers. Viewer lookups retain request cancellation.
- `/api/backdoor/finance/expense-reports` lists exactly the key owner's `/Expenses/Review` queue (unscoped `GetReviewQueueAsync(ct)` candidates filtered by the owner’s report View authorization and the existing active-year category scope before metadata is projected, narrowed by `year`/`status`). A report or attachment the owner's `View` check refuses is a 403, not a 404 — unlike Issues, a machine caller here is told a resource exists but is denied, since the finance-wide routes already disclose that much.
- The database never holds a plaintext key. `BackdoorApiKeyService` hashes on the way in and compares hashes on the way out.
- A key only works for someone a scope admits **whose account state is `Active`**, and only until its `ExpiresAt` — checked at issue, at rotation, **and on every authentication**. Each controller then checks its own scope: a good key whose holder that scope does not admit is 403, and a controller declaring no scope answers 403 to everyone. A role that expires, is revoked, or is swept by account deletion stops the key working on the next request, and so does suspension, which moves `users.State` while deliberately leaving role assignments standing. The row is refused, not revoked, so restoring the role or lifting the suspension restores the key. The key page shows such a key as **Disabled** and withholds Rotate, since rotation applies the same test. An expired key shows as **Expired**; rotating it is how it is renewed.
- Only the owner or a full Admin issues, rotates or revokes a key. `BackdoorController` enforces it through the `AdminOnly` policy on each POST, not only by hiding buttons; someone else's key reads as missing.
- Issue and revoke both write an audit entry naming the key and its owner (`BackdoorApiKeyIssued` / `BackdoorApiKeyRevoked`); a rotation is recorded as a revoke followed by an issue.
- Every controller here is an orchestrator: it calls another section's contracts interface and formats the result. None of them touch a repository or a `DbContext` other than through `IBackdoorApiKeyRepository`.
- A key-authed principal carries the `BackdoorApiKey` authentication scheme (`BackdoorApiKeyAuthFilter.AuthenticationScheme`). It never passes through the Shell's claims transformation, so its role claims come from the same snapshot the key service used for eligibility and it carries no state claims — and the Shell's onboarding gates (`NameRequiredFilter`, `MembershipRequiredFilter`) skip it — every key-authed controller is `[AllowAnonymous]` — rather than redirecting a JSON client to an HTML page.
- Every `PATCH /api/backdoor/issues/{id}/*` answers the same way whichever field moved: `{success:true}`, 404 for a missing item, 422 carrying the service's reason for a rejected change.

- Issues mutations map owner-section results to 404 for missing/inaccessible issues, 422 for rule refusals, and 500 for dependency failures. Comment posting uses an explicit missing result. Exceptions and their diagnostic text never select 404/422 or enter the response body.

## Negative Access Rules

- A caller with no `X-Api-Key` header **cannot** reach any endpoint — 401.
- A key whose owner lacks `Tasks.Manage` on any one team in a bulk-load payload **cannot** load any of it — 403 naming those teams, and a reference that matches no team is named the same way, so the answer never tells an unknown team from an unmanaged one; the payload is not checked further and nothing is created.
- A caller with an unknown or revoked key **cannot** reach any endpoint — 401, deliberately indistinguishable from the above. There is no "server not configured" status: keys are rows, not environment variables.
- An admin **cannot** recover a key's plaintext after issue, their own included.
- A former Admin or Board member **cannot** keep using a key issued while they held the role — 401 from the next request on, once no scope admits them.
- A holder below Admin and Board **cannot** use a key past its expiry — 401 — and **cannot** reach a Staff controller (logs, agent, issues, surveys, store, finance, notifications) — 403.
- A suspended Admin or Board member **cannot** use their key: the machine surface is exempt from the onboarding gates the account-status wall runs through, so eligibility is the only thing standing between a suspended account and the API.
- A member no scope admits **cannot** be issued a key, however the request is made, and gets 403 from `/Backdoor`.
- A non-Admin **cannot** issue, rotate or revoke someone else's key, Board included.
- Backdoor **cannot** read another section's tables. Anything it serves comes from that section's published contracts interface.

## Triggers

- On issue: an audit entry (`BackdoorApiKeyIssued`), and the plaintext is surfaced once through TempData to the key page.
- On revoke: an audit entry (`BackdoorApiKeyRevoked`) and `RevokedAt`/`RevokedByUserId` stamped.
- On rotate: revoke of the old key, then issue of a replacement carrying the same owner and label — two audit entries.
- On every successful authentication: `LastUsedAt` stamped on the key. An expired key, or one whose owner is no longer eligible, authenticates nothing and is not stamped.
- On GDPR erasure: the human's keys are hard-deleted, and they are detached as the creator or revoker of anyone else's — the row belongs to its owner, not to the admin who handled it.
- On account merge: the eliminated account's keys, and its actor columns, fold onto the survivor.

## Cross-Section Dependencies

- **Audit actions:** declared on `BackdoorAuditActions` (`src/Sections/Humans.Backdoor/BackdoorAuditActions.cs`), one `AuditActionDefinition` each, written through `IAuditLogService`: `BackdoorApiKeyIssued`, `BackdoorApiKeyRevoked`.
- **Agent**: reads transcripts via `IAgentTranscriptRead` (`Humans.Agent.Contracts`).
- **Issues**: reads and triages via `IIssueTriage` (`Humans.Issues.Contracts`).
- **Surveys**: reads definitions, exports and aggregates via `ISurveyAnalysisRead` (`Humans.Surveys.Contracts`).
- **Store**: reads the accounting export — order lines, settled payments and order history per year — via `IStoreAccountingRead` (`Humans.Store.Contracts`). Any active key may read order lines and payments; order history needs a holder who can administer the Store.
- **Expenses**: reads reports, lines, attachments and the review queue via `IExpenseReportService` (`Humans.Expenses/Contracts`, not the contracts leaf: a section reference, since `IExpenseReportService` itself lives there, not in a separate `.Contracts` project). Gated per route (see Invariants) — peterdrier/Humans#1838.
- **Finance**: reads creditor accounts, ledgers, the category map, SEPA transfers and the expense reconciliation via `IFinanceService` (`Humans.Finance.Contracts`). `FinancePolicies.Manage`-gated — peterdrier/Humans#1838. Expense-reconciliation responses order each member's open transfers by generation time, oldest first, in the response projection.
- **Holded**: `IHoldedService.GetPurchaseDocumentsAsync` for the purchase-document sync state on `holded-sync` (`Humans.Holded`'s `Contracts/` folder, a section reference).
- **Budget**: `IBudgetService.GetYearByIdAsync` for the budget-year label (e.g. "2026") an expense report is booked to, for the year filter/field — peterdrier/Humans#1838.
- **Tasks**: loads a set of tasks via `ITaskWrite` (`Humans.Tasks/Contracts`, a section reference). Before calling it the controller checks `Tasks.Manage` on every team in the payload through `IPermissionService` (`Humans.Permissions`), finding each team by slug or id through `ITeamService` (`Humans.Teams.Contracts`) and passing Tasks the id it checked; Tasks holds every other rule. A refusal is logged at Warning.
- **Notifications**: reads the key owner's unread inbox and meters via `INotificationInboxRead` (the `Humans.Notifications.Contracts` leaf, so Backdoor never references the Notifications section project). Rows are the owner's own; meters follow the owner's roles.
- **Auth**: `IRoleAssignmentService.GetActiveForUserAsync` supplies one snapshot for both scope checks and the role claims installed by `BackdoorApiKeyAuthFilter`; the filter performs no second role lookup. `GetActiveUserIdsInRoleAsync` supplies the admin page's recipient list, narrowed to active accounts so the dropdown never offers someone the service would refuse. Each listed key's usable/refused badge reads `BackdoorKeyRow.IsOwnerEligible`, which `ListAsync` computes with the same eligibility test as authentication.
- **AuditLog**: `IAuditLogService.LogAsync` for the key lifecycle.
- **Gdpr**: `IUserDataContributor` — `backdoor_api_keys` is user-keyed, so the section owes an Article 15 slice (`BackdoorApiKeyService.BackdoorApiKeys`, hash excluded) and an Article 17 erasure.
- **Users**: `IUserService.GetUserInfoAsync` for the account-state half of key eligibility and `GetUserInfosAsync` for display names on the admin page and on the API's issue, agent and finance projections (the finance report detail reads its one submitter through `GetUserInfoAsync`); `IUserMerge` to fold an eliminated account's keys onto the survivor.
- **Base**: `InMemoryLogSink` behind `/api/backdoor/logs`.

No section depends on Backdoor. It is a leaf, and deliberately so — the fan-in would otherwise be a cycle. The Shell references nothing in it.

## Architecture

**Owning services:** `BackdoorApiKeyService`
**Owned tables:** `backdoor_api_keys`
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
