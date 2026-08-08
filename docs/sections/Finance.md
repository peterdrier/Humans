<!-- freshness:triggers
  src/Humans.Application/Services/Finance/**
  src/Humans.Application/Interfaces/Finance/**
  src/Humans.Domain/Entities/HoldedExpenseDoc.cs
  src/Humans.Domain/Entities/HoldedCategoryMap.cs
  src/Humans.Domain/Entities/HoldedSyncState.cs
  src/Humans.Domain/Entities/HoldedLedgerLine.cs
  src/Humans.Infrastructure/Repositories/Finance/HoldedRepository.cs
  src/Humans.Infrastructure/Services/Holded/HoldedClient.cs
  src/Humans.Infrastructure/Jobs/HoldedSyncJob.cs
  src/Humans.Web/Controllers/FinanceController.cs
-->
<!-- freshness:flag-on-change
  FinanceController routes, auth policy (FinanceAdminOrAdmin), or budget-delegation correctness — review when FinanceController or its Budget/Tickets service dependencies change. Holded attribution logic (Account → Tag → Unmatched) and provisioning model reviewed when HoldedMatcher, IHoldedFinanceService, or HoldedCategoryMap change.
-->

# Finance — Section Invariants

Finance is the **treasurer's reality side** of the money story. Budget owns planning and public presentation; Finance owns actuals, reconciliation, and treasurer-facing operational data. The two share `BudgetGroup` / `BudgetCategory` keys; nothing else.

## Today vs Planned

**Today — treasurer surface over Budget** (built): `FinanceController` at `/Finance/*` is the treasurer's window over Budget data — Budget years, groups, categories, line items, ticketing projections, audit log, cash-flow view. Gated on `FinanceAdmin` or `Admin`. Reads/writes route through `IBudgetService`, `ITicketingBudgetService`, `ITicketServiceRead`.

**Today — Holded actuals integration** (built, Feature 1): Finance-owned entities (`HoldedExpenseDoc`, `HoldedCategoryMap`, `HoldedSyncState`) with a dedicated repository, `IHoldedFinanceService`/`HoldedFinanceService`, nightly sync job, and treasurer UI pages for account provisioning and unmatched-doc resolution. Actuals displayed on the budget year detail view.

**Today — Holded creditor ledger cache** (built, Feature 2): nightly sync of the Holded daybook (dailyledger) journal lines into one table, `HoldedLedgerLine`. Creditor balance, owed, and payments are **derived** from these lines (no separate balance/payment tables, no live API call on page load); `GetCreditorStatusAsync` / `GetCreditorLedgerAsync` expose the read surface to Expenses. See [Feature 2](#feature-2--holded-creditor-ledger-cache) below.

## Concepts

- A **Holded Expense Doc** is a purchase invoice pulled from Holded and stored verbatim. Each line is attributed to a budget category via the attribution chain below.
- **Attribution chain (Account → Tag → Unmatched):**
  1. **Account (A):** the line's booked Holded `account` id is looked up in `HoldedCategoryMap.HoldedAccountId`. Match → `MatchSource = Account`.
  2. **Tag (B):** each raw tag is normalized (lowercase, non-alphanumeric stripped — Holded strips separators like dashes) and compared against `HoldedCategoryMap.Tag`. First hit → `MatchSource = Tag`.
  3. **None:** doc lands in the **unmatched bucket** (`MatchStatus = Unmatched`, `MatchSource = None`).
- A **Holded Category Map** row joins a `BudgetCategory` to its dedicated Holded account number/id and its dash-free fallback tag. Retired rows are archived (`IsActive = false`); Holded accounts are never deleted.
- The **Provisioning page** (`/Finance/HoldedAccounts`) reconciles the live Holded chart-of-accounts against the local `HoldedCategoryMap`: diffs into Mapped / ToAdd / Orphan. "Add one (test)" / "Add all" create accounts in Holded + map rows locally. Additive only.
- The **Holded Sync State** is a singleton row tracking the operational state of the recurring sync job (`Idle / Running / Error`).
- The **Unmatched Queue** (`/Finance/HoldedUnmatched`) is the working surface where the treasurer inspects unattributed docs and triggers a re-sync.

## Data Model

### HoldedExpenseDoc

**Table:** `holded_expense_docs`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| HoldedDocId | string | Unique. Natural key for upsert. |
| DocNumber | string | e.g. `F260009` |
| ContactName | string | Vendor name, denormalized. |
| Date | LocalDate | From Holded `date` (epoch s, Europe/Madrid) |
| Subtotal | decimal | EUR, raw |
| Tax | decimal | EUR, raw (net of IVA − IRPF) |
| Total | decimal | EUR, raw |
| Currency | string(3) | Lowercase ISO; v1 only handles `eur` |
| ApprovedAt | Instant? | Null = not approved → excluded from actuals |
| TagsJson | string (jsonb) | Raw tag list from Holded |
| BookedAccountId | string? | First product line's Holded account id |
| BudgetCategoryId | Guid? | Attributed category (null = unmatched) |
| MatchStatus | HoldedMatchStatus | `Matched` or `Unmatched` |
| MatchSource | HoldedMatchSource | `None`, `Account`, or `Tag` |
| RawPayload | string (jsonb) | Full Holded JSON for debugging |
| LastSyncedAt | Instant | Updated every sync that touches this row |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

**Cross-section FKs:** `BudgetCategoryId` → `BudgetCategory` (Budget) — FK only, no navigation property. `OnDelete: Restrict`.

### HoldedCategoryMap

**Table:** `holded_category_map`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| BudgetCategoryId | Guid | FK-only, no nav (cross-section) |
| HoldedAccountNumber | int | Reserved account number in Holded |
| HoldedAccountId | string | Holded's internal account id |
| Tag | string | Dash-free normalized fallback tag (Holded strips separators) |
| IsActive | bool | `false` = archived; row kept for history |
| ArchivedAt | Instant? | Set when `IsActive` flipped to false |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

### HoldedLedgerLine

**Table:** `holded_ledger_lines`

Cached Holded daybook (dailyledger) journal line for a 400000xx creditor account — the **single source of truth** for creditor activity. Unique on `(EntryNumber, Line)` for idempotent upsert (journal lines are immutable facts); indexed on `AccountNum`. Fields: `EntryNumber`, `Line`, `AccountNum`, `Date` (Instant), `Type`, `Description`, `Debit`, `Credit`, plus sync bookkeeping. Refreshed by `SyncCreditorLedgerAsync` (full-history backfill on first run, incremental append nightly). Everything derives from these lines: balance = Σdebit − Σcredit (negative = org owes), owed = max(0, Σcredit − Σdebit), payments = debit lines, ins = credit lines.

### HoldedSyncState

**Table:** `holded_sync_states` (singleton, `Id = 1`)

Fields: `LastSyncAt`, `SyncStatus` (`Idle / Running / Error`), `LastError`, `StatusChangedAt`, `LastSyncedDocCount`.

### HoldedMatchStatus

| Value | Description |
|-------|-------------|
| Matched | Attributed to a `BudgetCategoryId` |
| Unmatched | No account or tag hit; sits in the unmatched bucket |

Stored as string via `HasConversion<string>()`.

### HoldedMatchSource

| Value | Description |
|-------|-------------|
| None | Unmatched (no attribution found) |
| Account | Attributed via the line's booked Holded account |
| Tag | Attributed via a normalized tag fallback |

### HoldedSyncStatus

| Value | Description |
|-------|-------------|
| Idle | Not currently running |
| Running | Sync in progress |
| Error | Last run threw; `LastError` populated |

## Routing

All routes are gated by `[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]` on `FinanceController`.

### Today — treasurer surface over Budget

| Route | Controller action |
|-------|-------------------|
| `GET /Finance` | `Index` — Budget year overview (active year) |
| `GET /Finance/Years/{id}` | `YearDetail` — Budget year detail (includes Holded actuals column) |
| `GET /Finance/Categories/{id}` | `CategoryDetail` — Budget category detail |
| `GET /Finance/AuditLog/{yearId?}` | `AuditLog` — Budget audit log |
| `GET /Finance/CashFlow` | `CashFlow` — Cash flow projection |
| `GET /Finance/Admin` | `Admin` — Budget admin (years/groups) |
| `POST /Finance/Years/{id}/SyncDepartments` | `SyncDepartments` |
| `POST /Finance/Years/Create` | `CreateYear` |
| `POST /Finance/Years/{id}/UpdateStatus` | `UpdateYearStatus` |
| `POST /Finance/Years/{id}/Update` | `UpdateYear` |
| `POST /Finance/Years/{id}/Delete` | `DeleteYear` |
| `POST /Finance/Groups/Create` | `CreateGroup` |
| `POST /Finance/Groups/{id}/Update` | `UpdateGroup` |
| `POST /Finance/Groups/{id}/Delete` | `DeleteGroup` |
| `POST /Finance/Categories/Create` | `CreateCategory` |
| `POST /Finance/Categories/{id}/Update` | `UpdateCategory` |
| `POST /Finance/Categories/{id}/Delete` | `DeleteCategory` |
| `POST /Finance/LineItems/Create` | `CreateLineItem` |
| `POST /Finance/LineItems/{id}/Update` | `UpdateLineItem` |
| `POST /Finance/LineItems/{id}/Delete` | `DeleteLineItem` |
| `POST /Finance/Years/{id}/EnsureTicketingGroup` | `EnsureTicketingGroup` |
| `POST /Finance/TicketingProjection/{groupId}/Update` | `UpdateTicketingProjection` |
| `POST /Finance/TicketingBudget/{yearId}/Sync` | `SyncTicketingBudget` |

### Holded integration

| Route | Purpose |
|-------|---------|
| `GET /Finance/HoldedAccounts` | Account provisioning UI (reconcile + apply) |
| `GET /Finance/HoldedUnmatched` | Unmatched-doc worklist with deep links and "Sync now" |
| `GET /Finance/Creditors` | Admin overview of all cached 400000xx creditor accounts with member bindings |
| `GET /Finance/Creditors/{accountNum:int}` | Per-account creditor statement (balance + itemized journal lines) |
| `POST /Finance/HoldedAccounts/Provision` | Add one or all pending Holded accounts + map rows |
| `POST /Finance/HoldedSync/Run` | Manual sync trigger |
| `POST /Finance/Creditors/Bind` | Manually bind a member to a Holded creditor account by 400000xx number |
| `POST /Finance/Creditors/Unbind` | Clear a member's creditor binding (the remedy for a wrong bind or a collision) |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| FinanceAdmin, Admin | Full access to all `/Finance/*` routes. View budget data, manage years/groups/categories/line items, trigger ticketing sync. Provision Holded accounts, trigger Holded sync, inspect unmatched docs. |
| Department coordinator | None — Finance routes are FinanceAdmin-only. |
| Any other authenticated human | None |

## Invariants

<!-- wheat: docs/superpowers/plans/2026-05-25-holded-finance-feature1-actuals.md §Task 7 / self-review -->
- A purchase doc is attributed **as a whole, by its first product line's** booked account (plus the union of doc-level and line-level tags), and its full `Total` lands on that one category. A multi-line doc booked across several Holded accounts is not split; line-level attribution is a deliberate later refinement (`HoldedFinanceService.MapDoc`).
- Actuals are keyed on the **calendar year** of the doc's Europe/Madrid date, matched against `BudgetYear.Year` parsed as an integer (`FinanceController` → `GetActualsForYearAsync` → `HoldedRepository.GetMatchedForYearAsync`). A budget year whose `Year` string is not a plain number, or that does not run January–December, shows no actuals.
- Only `FinanceAdmin` or `Admin` may access any `/Finance/*` route (`[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]` on `FinanceController`).
- All budget mutations in `FinanceController` route through `IBudgetService` — the controller owns no Finance-domain tables beyond the Holded integration.
- The sync job pulls all purchase docs from Holded each cycle (full-pull). Upsert is keyed on `HoldedDocId`; `CreatedAt` is preserved across re-syncs.
<!-- wheat: docs/superpowers/specs/2026-04-26-holded-read-integration-design.md §Holded API findings -->
- Full-pull is forced by a Holded API limitation (live probe, 2026-04-26): the purchase-documents endpoint's only date filters (`?starttmp`/`?endtmp`) filter on `accountingDate`, which is null on most real purchase docs, so there is no reliable incremental-sync key for purchase documents. `ListPurchaseDocumentsPageAsync` therefore takes only `page`/`limit`. (The dailyledger endpoint is different — its `starttmp`/`endtmp` window sweep works and is used by the creditor-ledger sync.)
- Attribution runs every sync. Fixing an account mapping or tag in Holded takes effect on next sync or via the manual "Sync Now" button.
- Attribution order: **Account** (booked line account id) → **Tag** (normalized, dash-free) → **Unmatched**. First match wins.
- Tags are normalized: lowercase, all non-alphanumeric characters stripped (Holded strips separators like dashes from tag values).
- Provisioning is additive only. Retiring a map entry sets `IsActive = false`; it does not delete the Holded account.
- `HoldedExpenseDoc.Total` is included in category-level actuals only when `ApprovedAt IS NOT NULL`.
- Holded API key read from env var `HOLDED_API_KEY` only — never `appsettings.json`.
<!-- wheat: docs/superpowers/specs/2026-05-25-holded-finance-integration-design.md §3 Link & data -->
- The member ↔ creditor-account link resolves through the Holded contact's `supplierRecord.num` field, never by name matching. It is attempted **exactly once**, best-effort, during outbox processing after the payable exists (`ExpenseReportService` → `IHoldedClient.GetContactAsync`); a failure or a null `num` is logged, the null link is stored, and the outbox event is still marked processed so a created doc is never stranded as permanently-failed. **There is no automatic retry** — `SyncCreditorLedgerAsync` imports daybook lines but never re-resolves the contact — so after an initial miss the member stays unlinked until someone runs `POST /Finance/Creditors/Bind`, or a later report from the same member resolves it and backfills the member-level binding (nobodies-collective/Humans#972). `ListCreditorAccountsAsync` returns exactly these unresolved bindings as the `Unresolved` half of its result — they have no account row to sit on, so the account list alone cannot show them — and they render in their own card on `/Finance/Creditors`, making the manual step discoverable rather than silent.
- A 400000xx account — and the Holded contact behind it — binds to **at most one member**. All three write paths test for a conflicting binding (`FindConflictingBinding`, on both the account number and the contact id: after the one-shot number resolution misses, a binding carries a contact id with a null `SupplierAccountNum`, which an account-number-only check cannot see). They differ in the remedy, because only one of them is a guess:
  - **`SetCreditorContactAsync`** (manual bind) — an admin picked the account, so the pick can be wrong: **refuse and write nothing** (nobodies-collective/Humans#974).
  - **`SetCreditorAccountNumAsync`** (automatic, after the push resolves `supplierRecord.num` from a live `GetContactAsync`) — Holded assigned that number to the contact just pushed, so Holded is authoritative and the *older* binding is the wrong guess. Refusing would strand a created payable against a wrong row, so it **writes the truth, logs Error, and leaves the collision standing** on `/Finance/Creditors` for a human (nobodies-collective/Humans#975).
  - The **`seedContactId` / `seedAccountNum` lazy-seed** through **`EnsureCreditorContactAsync`** — this is *our own cache* off the member's prior report, not something Holded just told us, so it is a guess and gets the manual bind's treatment: a seed landing on another member's binding is **refused and dropped**, and the push mints this member their own Holded contact instead. This is also what makes Unbind durable — see below.
- The DB index on `SupplierAccountNum` is deliberately **non-unique**. A unique index would turn a data anomaly into a `DbUpdateException` inside unattended outbox drain — stranding a created Holded doc as permanently-failed — and would have to be created against production rows that may already collide. Enforcement lives in the service (`memory/architecture/db-enforcement-minimal.md`).
- `ListCreditorAccountsAsync` returns **every** binding on an account (`HoldedCreditorAccountRow.Bindings`), not the first, and decides a binding's row **through the Holded contact id**, falling back to the stored `SupplierAccountNum` only for a contact Holded's list does not carry. Which 400000xx a contact holds is Holded's fact, and resolving through it does two jobs. A binding whose number never resolved reaches its row at all — keyed on the number alone the account renders "unbound" while a member in fact holds the contact behind it (the invisibility the #974 second guard's error message ran into), and such a binding could not be unbound from the page. And because the two columns are independent, bindings sharing a contact can carry numbers that disagree; the contact resolution lands them on one row, so the **contact-id half** of the invariant surfaces as a collision instead of two innocent-looking single-member rows. It depends on the live Holded contact list, so it degrades with the names when Holded is unreachable. `/Finance/Creditors` renders each bound member with an **Unbind** button (`POST /Finance/Creditors/Unbind` → `ClearCreditorContactAsync`) and sorts collisions to the top. Unbind removes the whole binding row rather than nulling `SupplierAccountNum`: a binding stripped of its number still carries the other member's Holded contact id, which merges their payables just as thoroughly. The member's next push re-resolves the contact from scratch.
- **Unbind is durable against restoring another member's binding, not against re-deriving the member's own.** Deleting the row is not by itself enough: `ProcessHoldedCreateAsync` seeds the next push from the cleared member's prior report, which still carries whatever contact id and 400000xx were cached on it. The seed-refusal above is what closes that loop — after unbinding a wrong binding, the seed points at the other member's contact, is refused, and the member gets their own new Holded contact. A member's *own* contact still re-derives from their linked history on the next push; that is the documented lazy-seed self-heal and it restores the correct value, not a wrong one.
- **Unbind holds against a push already in flight, in the steady state only.** `ProcessHoldedCreateAsync` spans several Holded calls, so the drain can be mid-push for tens of seconds while an admin clicks Unbind. `EnsureCreditorContactAsync` therefore writes nothing when the member already holds the contact it just PUT and the binding already carries its 400000xx: `UpsertContactAsync` returns the id it was given and `Source`/number come off the binding just read, so the only column that would change is `UpdatedAt`, which nothing reads — and writing it would resurrect a binding the admin cleared, from the copy read before they clicked. Not yet safe in general: a binding still missing its number, and `SetCreditorAccountNumAsync`, write real content and can still lose a concurrent delete (nobodies-collective/Humans#995 — the fix is an update-only repository write, not a version column; see [`no-concurrency-tokens`](../../memory/architecture/no-concurrency-tokens.md)).
- Creditor accounts are the `40000000`–`40000099` block (`CreditorAccountMin`/`Max`). Every read that draws on Holded's contact list must filter to it — Holded assigns a supplier number to *every* supplier contact, so an unfiltered list turns ordinary org vendors into bindable member creditor accounts. `SetCreditorContactAsync` validates the posted number against the block server-side — the filtered dropdown is not a gate.
- **Unbound is a valid state, not an error.** A first-time submitter has no creditor contact until their first push; `EnsureCreditorContactAsync` creates it and `SetCreditorAccountNumAsync` records the assigned 400000xx. The bind control exists only for a *pre-existing* Holded contact the auto-create would duplicate. Unbound does **not** imply no contact: `holded_creditor_contacts` was created empty (no backfill), so a member linked before it existed carries a contact id on their older reports only. `ProcessHoldedCreateAsync` therefore seeds `EnsureCreditorContactAsync` from the member's most recent linked report when the report being pushed has no contact id of its own — a null seed makes the client POST a second contact and splits their payables. That push writes the missing binding, so the gap self-heals on first interaction rather than by data migration.
- `ListCreditorAccountsAsync` reads the Holded contact list (`ListContactsAsync`) through a 2-minute `IMemoryCache` entry (design-rules §15 Option A, `CacheKeys.HoldedContacts`) rather than calling Holded live on every load — the 400000xx account **name** lives only in Holded, and the short TTL keeps a contact created today visible without a nightly-cache lag or a per-request call. `ListContactsAsync` itself paginates internally (walks `page` until an empty page returns), so the cached list is never silently truncated. It degrades to blank names when the Holded call fails — transport failure, a rejected key, or an unreadable body — rather than failing the page; unexpected exception types propagate.
- **Never index a nested Holded JSON node directly.** Holded serializes an absent sub-record as an empty *array* (`"supplierRecord": []`), and `JsonNode`'s string indexer throws `InvalidOperationException` on anything that is not a `JsonObject`. Combined with the degrade-to-blank rule above, one such contact blanked every account name on the bind card and `/Finance/Creditors` in production (nobodies-collective/Humans#994). `HoldedClient.ParseContact` reads through `Prop(node, name)`, which yields null for a non-object, and the list parse isolates each contact so one unreadable row costs only its own name. `ListCreditorAccountsAsync` logs when *no* account resolved a name — the all-or-nothing signature of this failure — since it was otherwise silent until a human noticed.

## Negative Access Rules

- Coordinators **cannot** view `/Finance/*` routes.
- The sync job **cannot** delete `HoldedExpenseDoc` rows. Holded-side deletions are not handled in v1.
- Finance **cannot** read or write Budget tables directly — all cross-section access goes through `IBudgetService` (tech debt: future read-split to `IBudgetServiceRead` noted as Feature 2 work).
- Finance **cannot** write to `holded_expense_docs` outside the sync job. No manual create/edit/delete UI for expense docs in v1.

## Triggers

- None in the Finance domain layer for the budget side. Budget mutations via `FinanceController` trigger Budget-section side effects (audit log entries written by `IBudgetService`).
- When the sync job starts, `HoldedSyncState.SyncStatus` flips to `Running`. On success returns to `Idle` with `LastSyncAt` and `LastSyncedDocCount` updated. On exception goes to `Error` with `LastError` populated; next scheduled run retries.

## Cross-Section Dependencies

- **Budget:** `IBudgetService` (read + write — all budget year/group/category/line-item mutations in `FinanceController` route through it), `ITicketingBudgetService` (ticketing projection and actuals sync). Also used by `IHoldedFinanceService` for category lookups (tech debt; see Planned above).
- **Tickets:** `ITicketServiceRead.GetTicketOrdersAsync` (cash flow view derives gross paid revenue from `TicketOrderInfo`).

Budget never calls into Finance.

## Architecture

**Status:** (A) — Finance has Application-layer services, an owned repository, and an EF migration.

**Owning service:** `IHoldedFinanceService` / `HoldedFinanceService`  
**Pure matcher:** `HoldedMatcher` (static, no dependencies)  
**Owned repository:** `IHoldedRepository` / `HoldedRepository`  
**Owned tables:** `holded_expense_docs`, `holded_category_map`, `holded_sync_states`, `holded_ledger_lines`, `holded_creditor_contacts`  
**Job:** `HoldedSyncJob` (cron `0 3 * * *`)  
**Migrations:** `20260715103643_BaselineFinance` — consolidated onto `FinanceDbContext` (its own history table, `__EFMigrationsHistory_Finance`) when Finance moved off the shared `HumansDbContext` (nobodies-collective/Humans#858); the earlier per-feature migration chain (`HoldedActuals`, `HoldedCreditorData`, `HoldedCreditorContact`, `HoldedLedgerSingleSource`) was squashed into this baseline  
**Architecture tests:** `tests/Humans.Application.Tests/Architecture/FinanceArchitectureTests.cs`

> **What exists (Feature 1):**
> - `src/Humans.Web/Controllers/FinanceController.cs` — Budget admin + treasurer view + Holded routes. Injects `IBudgetService`, `ITicketingBudgetService`, `ITicketServiceRead`, `IHoldedFinanceService`.
> - `PolicyNames.FinanceAdminOrAdmin` and `RoleNames.FinanceAdmin` — role + policy wired in `AuthorizationPolicyExtensions.cs`.
> - `src/Humans.Domain/Entities/HoldedExpenseDoc.cs`
> - `src/Humans.Domain/Entities/HoldedCategoryMap.cs`
> - `src/Humans.Domain/Entities/HoldedSyncState.cs`
> - `src/Humans.Domain/Enums/HoldedMatchStatus.cs`, `HoldedMatchSource.cs`, `HoldedSyncStatus.cs`
> - `src/Humans.Application/Services/Finance/HoldedFinanceService.cs`
> - `src/Humans.Application/Services/Finance/HoldedMatcher.cs`
> - `src/Humans.Application/Interfaces/Finance/IHoldedFinanceService.cs`
> - `src/Humans.Application/Interfaces/Repositories/IHoldedRepository.cs`
> - `src/Humans.Infrastructure/Repositories/Finance/HoldedRepository.cs`
> - `src/Humans.Infrastructure/Services/Holded/HoldedClient.cs`
> - `src/Humans.Infrastructure/Jobs/HoldedSyncJob.cs`
> - `tests/Humans.Application.Tests/Architecture/FinanceArchitectureTests.cs`
> - EF migration `20260525163748_HoldedActuals` for all three Feature 1 Finance-owned tables
>
> **What exists (Feature 2 — ledger single-source):**
> - `src/Humans.Domain/Entities/HoldedLedgerLine.cs` — the cached daybook line (everything derives from these)
> - `src/Humans.Domain/Entities/HoldedCreditorContact.cs` — member → 400000xx binding (from #1021)
> - `src/Humans.Application/Services/Finance/Dtos/HoldedPaymentInfo.cs` — read-only row DTO (date / amount / document type) surfaced on `HoldedCreditorStatus.Payments` (derived from debit lines)
> - `IHoldedRepository.UpsertLedgerLinesAsync`, `GetLedgerLinesByAccountNumAsync`, `GetAllLedgerLinesAsync`, `GetLatestLedgerLineDateAsync`
> - `IHoldedFinanceService.SyncCreditorLedgerAsync` — nightly cache refresh (called from `HoldedSyncJob`)
> - `IHoldedFinanceService.GetCreditorStatusAsync(int? supplierAccountNum)` / `GetCreditorLedgerAsync(int supplierAccountNum)` — Expenses→Finance read surface, derived from cached lines; `HoldedCreditorStatus` carries `Payments` (debit lines, for the per-member ledger)
> - `IHoldedFinanceService.ListCreditorAccountsAsync` — returns `(Accounts, Unresolved)`; the `Unresolved` half is the bindings with no resolved 400000xx, surfaced on `/Finance/Creditors` for manual bind (nobodies-collective/Humans#972)
> - `IHoldedClient.GetContactAsync`, `ListContactsAsync`, `ListDailyLedgerAsync`, `UpsertContactAsync` — Holded API surface (the chartofaccounts/payments calls were removed)

### Feature 2 — Holded creditor ledger cache

`SyncCreditorLedgerAsync` runs nightly as part of `HoldedSyncJob`. On the first run (empty cache) it backfills full history by sweeping the daybook in ≤1-year backward windows until an empty window; thereafter it appends incrementally from the latest cached line. Only `400000xx` creditor lines are stored (the dailyledger has no server-side account filter, so the fetch sweeps the whole daybook regardless). Page loads read `holded_ledger_lines` from Postgres and aggregate — **zero Holded calls per view**; the API cost is a fixed nightly job, independent of traffic. The admin creditor overview additionally reads the cached Holded contact list for account names — see Invariants.

The Expenses section reads creditor status via `GetCreditorStatusAsync(supplierAccountNum)` and the statement via `GetCreditorLedgerAsync(supplierAccountNum)`. Both derive from the cached lines: balance = Σdebit − Σcredit (balance ≥ 0 = settled), owed = max(0, −balance), payments = debit lines. `HoldedCreditorStatus.Payments` (`IReadOnlyList<HoldedPaymentInfo>?`) is mapped from the debit lines and consumed read-only by the Expenses dashboard ledger (`GetHoldedTimelineAsync`).

**Org-accounting boundary (HARD):** Humans only reads the Holded daybook. It never writes debt-reassignment journal entries or modifies the chart of accounts to reflect internal transfers. `holded_ledger_lines` is a read-through cache of immutable journal facts, not a ledger Humans writes to.

### Owned repository

- **`IHoldedRepository`** — owns `holded_expense_docs`, `holded_category_map`, `holded_sync_states`, `holded_ledger_lines`, `holded_creditor_contacts`
  - No cross-domain navs: `BudgetCategoryId` and `HoldedCreditorContact.UserId` are FK-only, no navigation property
  - Ledger lines upsert idempotently on `(EntryNumber, Line)`; expense docs upsert (full overwrite on re-sync)

### Current violations

None. `FinanceController` calls Budget/Tickets via their service interfaces. `HoldedFinanceService` calls `IBudgetService` for cross-section reads (acknowledged tech debt; future read-split to `IBudgetServiceRead` noted). No cross-section DbContext reads.

### Touch-and-clean guidance

- **Soft boundary:** `TicketingProjection` and `TicketingBudgetService` are conceptually "actuals materialization" but live in Budget today. Treat as known soft boundary — separate cleanup, not an active violation.
- **Future:** split Holded's `IBudgetService` dependency to `IBudgetServiceRead` and introduce `IBudgetServiceRead` where only reads are needed.
