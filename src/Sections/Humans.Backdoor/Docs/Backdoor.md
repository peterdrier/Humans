<!-- freshness:triggers
  src/Sections/Humans.Backdoor/**
  src/Humans.Web/Authorization/MembershipRequiredFilter.cs
  src/Humans.Web/Authorization/NameRequiredFilter.cs
  src/Sections/Humans.Agent/Contracts/IAgentTranscriptRead.cs
  src/Sections/Humans.Feedback/Contracts/IFeedbackTriage.cs
  src/Sections/Humans.Issues/Contracts/IIssueTriage.cs
  src/Sections/Humans.Notifications/Contracts/INotificationInboxRead.cs
  src/Sections/Humans.Surveys/Contracts/ISurveyAnalysisRead.cs
  src/Sections/Humans.Store/Contracts/IStoreAccountingRead.cs
  src/Sections/Humans.Expenses/Contracts/IExpenseReportServiceRead.cs
  src/Sections/Humans.Finance.Contracts/IHoldedFinanceServiceRead.cs
-->
<!-- freshness:flag-on-change
  Re-read the surface table and the auth model whenever a controller, the key service or the auth filter changes: the routes are a published contract for agents, and the "one key = one human" rule is the whole point of the section.
-->

# Backdoor — Section Invariants

The machine surface. Every key-authed API an agent talks to lives here, under `/api/backdoor/*`, gated by one personal key per human.

## Concepts

- A **Backdoor API key** is a credential issued to a *person*, not a service. Its plaintext exists only at the moment of issue; the database keeps a SHA-256 hash and a 12-character display prefix.
- **Issue / rotate / revoke** are the whole lifecycle. There is no "read the key back" — a lost key is rotated.
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
| CreatedByUserId | Guid? | The admin who allocated it; nulled on that admin's erasure |
| LastUsedAt | Instant? | Stamped on every successful resolve |
| RevokedAt | Instant? | Null means active |
| RevokedByUserId | Guid? | |

**Indexes / constraints:** unique on `KeyHash` (a presented key must resolve to at most one row); non-unique on `UserId`.

**Cross-section FKs:** `UserId`, `CreatedByUserId`, `RevokedByUserId` → `Users.User` (Users), as bare Guids — the Identity tables stay in `UsersDbContext`.

## Routing

| Route | Access | Serves |
|-------|--------|--------|
| `/api/backdoor/logs` | read | The in-memory log ring (`InMemoryLogSink`, Base) |
| `/api/backdoor/agent/conversations` | read | Agent conversation transcripts — list, one conversation, its messages; list previews retain a 200 UTF-16 code-unit cap without splitting Unicode surrogate pairs — via `IAgentTranscriptRead` |
| `/api/backdoor/issues` | read + write | The issue queue, via `IIssueTriage` |
| `/api/backdoor/feedback` | read + write | The feedback queue, via `IFeedbackTriage` |
| `/api/backdoor/surveys` | read | Survey definitions, responses and aggregates, via `ISurveyAnalysisRead` |
| `/api/backdoor/store/order-lines?year=` | read | One row per Store order line of the year — camp and team orders — with effective price, VAT, revenue account and invoice number, via `IStoreAccountingRead` |
| `/api/backdoor/store/payments?year=` | read | One row per `Paid` Store payment of the year (refunds negative) with its Stripe payment intent id, via `IStoreAccountingRead` |
| `/api/backdoor/finance/expense-reports?year=&status=` | read | The key owner's expense-report review queue — everything `/Expenses/Review` would show them, filterable by budget year and status — via `IExpenseReportServiceRead.GetReviewQueueAsync` |
| `/api/backdoor/finance/expense-reports/{id}` | read | One report in full, including its lines, gated by the same `View` check `/Expenses` uses. The payee name, masked IBAN and Holded timeline fields are further narrowed to the report's submitter (payment half) or a finance admin (push half); the Holded contact/supplier-account/doc ids are finance-admin only — the same split `/Expenses` shows in the browser; a viewer whose `View` passes on another ground (e.g. a category coordinator) gets none of them |
| `/api/backdoor/finance/expense-reports/{id}/attachments/{attachmentId}` | read | The stored bytes of one attachment, same `View` gate as the owning report |
| `/api/backdoor/finance/creditor-accounts` | read | Every 400000xx creditor account, its bindings (all of them) and the bindings with no account at all, via `IHoldedFinanceServiceRead.ListCreditorAccountsAsync` |
| `/api/backdoor/finance/creditor-accounts/{num}/ledger` | read | One account's balance and cached journal lines, contact header included, via `GetCreditorLedgerAsync` |
| `/api/backdoor/finance/category-map` | read | Every live `holded_category_map` row, via `GetCategoryMapAsync` |
| `/api/backdoor/finance/sepa-transfers` | read | Every generated SEPA transfer with its booking state, via `GetSepaTransfersAsync` |
| `/api/backdoor/finance/holded-sync` | read | The purchase-doc sync's state plus the docs it could not match, via `GetDocSyncInfoAsync` + `GetUnmatchedAsync` |
| `/api/backdoor/notifications` | read | The key owner's unread notifications, newest first, and the live meters their roles unlock, via `INotificationInboxRead`. Polling marks nothing read |
| `/Backdoor` | Admin UI | Allocate, rotate and revoke keys |

Authentication is the `X-Api-Key` header on every `/api/backdoor/*` request. There is no cookie path in and no anonymous endpoint.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Holder of an active key | Everything the APIs expose to that person, acting as themselves and scoped to their own roles |
| Board member | May be issued a key |
| Admin | All Board capabilities. Additionally: allocate, rotate and revoke anyone's key from `/Backdoor` |

## Invariants

- Issues list/detail/comments GETs and legacy Feedback list/detail/messages GETs pass request cancellation to their existing read contracts, including issue display-name reads. Write calls retain their existing cancellation boundaries.

- A key resolves to exactly one human, and that human is installed as the request principal — `ClaimTypes.NameIdentifier` plus one `ClaimTypes.Role` claim per active role assignment — so every write records a real actor and every log line is enriched with them. The Issues queue read consults those role claims to scope its result (see below), and the Notifications read passes the whole principal on so its meters are role-gated the same way the bell is; Agent, Feedback, Logs, Store, and Surveys reads do not. Finance's routes authorize imperatively: each action calls `IAuthorizationService.AuthorizeAsync` against the installed principal — `PolicyNames.FinanceAdminOrAdmin` for the finance-wide routes (creditor accounts, ledger, category map, SEPA transfers, Holded sync), and `PolicyNames.ExpenseReportView` (a named policy Expenses' own `SectionPolicies` registers, wrapping its internal `ExpenseReportOperationRequirement(View)`/handler) for a single report or attachment — the same check `ExpensesController` uses, not a bespoke Backdoor check (peterdrier/Humans#1838).
- Every `/api/backdoor/issues/*` route is fetched as the key's owner — id, roles and admin flag — so a Board-only key lists the Board-only queue, and an issue whose id it happens to hold but whose queue would not list it is a 404 to read, to comment on and to patch. Issues enforces that itself, on the same `IssueSectionRouting.CanHandle` the browser reads, so a key reaches exactly as far as its holder does in the browser.
- `/api/backdoor/finance/expense-reports` lists exactly the key owner's `/Expenses/Review` queue (`GetReviewQueueAsync(ownerId, isFinanceAdmin)`, narrowed by `year`/`status`). A report or attachment the owner's `View` check refuses is a 403, not a 404 — unlike Issues, a machine caller here is told a resource exists but is denied, since the finance-wide routes already disclose that much.
- The database never holds a plaintext key. `BackdoorApiKeyService` hashes on the way in and compares hashes on the way out.
- A key only works for a full Admin or a Board member **whose account state is `Active`** — checked at issue, at rotation, **and on every authentication**. A role that expires, is revoked, or is swept by account deletion stops the key working on the next request, and so does suspension, which moves `users.State` while deliberately leaving role assignments standing. The row is refused, not revoked, so restoring the role or lifting the suspension restores the key. The admin page shows such a key as **Disabled** and withholds Rotate, since rotation applies the same test.
- Issue and revoke both write an audit entry naming the key and its owner (`BackdoorApiKeyIssued` / `BackdoorApiKeyRevoked`); a rotation is recorded as a revoke followed by an issue.
- Every controller here is an orchestrator: it calls another section's contracts interface and formats the result. None of them touch a repository or a `DbContext` other than through `IBackdoorApiKeyRepository`.
- A key-authed principal carries the `BackdoorApiKey` authentication scheme (`BackdoorAuthentication.SchemeName`). It never passes through the Shell's claims transformation, so its role claims come from the auth filter's own lookup and it carries no state claims — and the Shell's onboarding gates (`NameRequiredFilter`, `MembershipRequiredFilter`) skip it rather than redirecting a JSON client to an HTML page.
- Every `PATCH /api/backdoor/{issues,feedback}/{id}/*` answers the same way whichever field moved: `{success:true}`, 404 for a missing item, 422 carrying the service's reason for a rejected change.

## Negative Access Rules

- A caller with no `X-Api-Key` header **cannot** reach any endpoint — 401.
- A caller with an unknown or revoked key **cannot** reach any endpoint — 401, deliberately indistinguishable from the above. There is no "server not configured" status: keys are rows, not environment variables.
- An admin **cannot** recover a key's plaintext after issue, their own included.
- A former Admin or Board member **cannot** keep using a key issued while they held the role — 401 from the next request on.
- A suspended Admin or Board member **cannot** use their key: the machine surface is exempt from the onboarding gates the account-status wall runs through, so eligibility is the only thing standing between a suspended account and the API.
- A plain member or volunteer **cannot** be issued a key, however the request is made.
- Backdoor **cannot** read another section's tables. Anything it serves comes from that section's published contracts interface.

## Triggers

- On issue: an audit entry (`BackdoorApiKeyIssued`), and the plaintext is surfaced once through TempData to the admin page.
- On revoke: an audit entry (`BackdoorApiKeyRevoked`) and `RevokedAt`/`RevokedByUserId` stamped.
- On rotate: revoke of the old key, then issue of a replacement carrying the same owner and label — two audit entries.
- On every successful authentication: `LastUsedAt` stamped on the key. A key whose owner is no longer eligible authenticates nothing and is not stamped.
- On GDPR erasure: the human's keys are hard-deleted, and they are detached as the creator or revoker of anyone else's — the row belongs to its owner, not to the admin who handled it.
- On account merge: the eliminated account's keys, and its actor columns, fold onto the survivor.

## Cross-Section Dependencies

- **Agent**: reads transcripts via `IAgentTranscriptRead` (`Humans.Agent.Contracts`).
- **Feedback**: reads and triages via `IFeedbackTriage` (`Humans.Feedback.Contracts`).
- **Issues**: reads and triages via `IIssueTriage` (`Humans.Issues.Contracts`).
- **Surveys**: reads definitions, exports and aggregates via `ISurveyAnalysisRead` (`Humans.Surveys.Contracts`).
- **Store**: reads the accounting export — order lines and settled payments per year — via `IStoreAccountingRead` (`Humans.Store.Contracts`). Any active key may read; there is no role scoping.
- **Expenses**: reads reports, lines, attachments and the review queue via `IExpenseReportServiceRead` (`Humans.Expenses/Contracts`, not the contracts leaf: a section reference, since `IExpenseReportServiceRead` itself lives there, not in a separate `.Contracts` project). Gated per route (see Invariants) — peterdrier/Humans#1838.
- **Finance**: reads creditor accounts, ledgers, the category map and SEPA transfers via `IHoldedFinanceServiceRead` (`Humans.Finance.Contracts`). `FinanceAdminOrAdmin`-gated — peterdrier/Humans#1838.
- **Budget**: `IBudgetServiceRead.GetYearByIdAsync` for the budget-year label (e.g. "2026") an expense report is booked to, for the year filter/field — peterdrier/Humans#1838.
- **Notifications**: reads the key owner's unread inbox and meters via `INotificationInboxRead` (`Humans.Notifications/Contracts`, not the contracts leaf: Backdoor is its only consumer). Rows are the owner's own; meters follow the owner's roles.
- **Auth**: `IRoleAssignmentService.IsUserAdminAsync` / `IsUserBoardMemberAsync` for key eligibility, `GetActiveForUserAsync` for the owner's role claims installed by `BackdoorApiKeyAuthFilter`, and `GetActiveUserIdsInRoleAsync` for the admin page's recipient list — narrowed there to active accounts, so the dropdown never offers someone the service would refuse and each listed key shows whether it still authenticates.
- **AuditLog**: `IAuditLogService.LogAsync` for the key lifecycle.
- **Gdpr**: `IUserDataContributor` — `backdoor_api_keys` is user-keyed, so the section owes an Article 15 slice (`BackdoorApiKeyService.BackdoorApiKeys`, hash excluded) and an Article 17 erasure.
- **Users**: `IUserServiceRead.GetUserInfoAsync` for the account-state half of key eligibility and `GetUserInfosAsync` for display names on the admin page and on the API's issue, agent and finance projections (the finance report detail reads its one submitter through `GetUserInfoAsync`); `IUserMerge` to fold an eliminated account's keys onto the survivor.
- **Base**: `InMemoryLogSink` behind `/api/backdoor/logs`.

No section depends on Backdoor. It is a leaf, and deliberately so — the fan-in would otherwise be a cycle. The Shell reads one constant from it, `BackdoorAuthentication.SchemeName`, so its onboarding gates can tell a machine request from a browsing session.

## Architecture

**Owning services:** `BackdoorApiKeyService`
**Owned tables:** `backdoor_api_keys`
**Status:** (A) Migrated — created at this shape in nobodies-collective/Humans#1128 (2026-08).

### Cross-section read interface

None. No other section consumes Backdoor; its whole surface is HTTP, and its service interface is section-internal.

| Read interface | Methods | Notes |
|---|---:|---|
| — | — | Not cross-section-consumed |

- `BackdoorApiKeyService` never imports `Microsoft.EntityFrameworkCore`; `IBackdoorApiKeyRepository` (Singleton + `IDbContextFactory`, §15b) is the only path to the table.
- Rotation changes the old row's revocation fields and inserts its replacement in one repository `SaveChangesAsync` call. A concurrent or already-revoked old key writes neither row, so a failed rotation never strands its owner without a credential.
- **Decorator decision** — no caching decorator. Key lookups are one indexed hash probe per API request at a handful of requests per minute, and a cache would have to be invalidated on every revoke to stay correct about the thing that matters most.
- **Display stitching** — `IUserServiceRead.GetUserInfosAsync`.
