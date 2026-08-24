<!-- freshness:triggers
  src/Sections/Humans.Finance/**
  src/Sections/Humans.Finance.Contracts/**
  src/Sections/Humans.Holded/Services/HoldedClient.cs
  src/Sections/Humans.Holded/Services/Service.cs
  src/Sections/Humans.Holded/Jobs/HoldedSyncJob.cs
  src/Sections/Humans.Budget/Controllers/BudgetAdminController.cs
-->
<!-- freshness:flag-on-change
  FinanceController routes or auth policy (FinanceAdminOrAdmin) — review when FinanceController or the section's cross-section contracts (IBudgetServiceRead, IHoldedService, IUserServiceRead) change. Holded attribution logic (Account → Tag → Unmatched) and the provisioning model reviewed when HoldedMatcher, IHoldedFinanceService, or HoldedCategoryMap change.
-->

# Finance — Section Invariants

Finance is the **treasurer's reality side** of the money story. Budget owns planning and public presentation; Finance owns actuals, reconciliation, and treasurer-facing operational data. The two share `BudgetGroup` / `BudgetCategory` keys; nothing else.

## Today vs Planned

**Today — treasurer surface over Budget** (built, *not this section*): the Budget years/groups/categories/line-items/cash-flow surface under the same `/Finance` prefix is `Humans.Budget`'s `BudgetAdminController` — see [`Budget.md`](../../Humans.Budget/Docs/Budget.md). It shares only the URL prefix and the `FinanceAdminOrAdmin` policy.

**Today — Holded actuals integration** (built, Feature 1): Finance-owned entities (`HoldedExpenseDoc`, `HoldedCategoryMap`, `HoldedDocSyncState`) with a dedicated repository, `IHoldedFinanceService` implemented by `Service`, a nightly sync job, and treasurer UI pages for account provisioning and unmatched-doc resolution. Actuals displayed on the budget year detail view.

**Today — Holded creditor reads** (built, Feature 2): the daybook journal-line mirror itself belongs to the **Holded** section; Finance derives creditor balance, owed and payments from it (no balance/payment tables of its own, no live API call on page load), and `GetCreditorStatusAsync` / `GetCreditorLedgerAsync` expose that read surface to Expenses. See [Feature 2](#feature-2--creditor-reads-over-the-holded-sections-mirror) below.

## Concepts

- A **Holded Expense Doc** is a purchase invoice pulled from Holded and stored verbatim. Each line is attributed to a budget category via the attribution chain below.
- **Attribution chain (Account → Tag → Unmatched):**
  1. **Account (A):** the line's booked Holded `account` id is looked up in `HoldedCategoryMap.HoldedAccountId`. Match → `MatchSource = Account`.
  2. **Tag (B):** each raw tag is normalized (lowercase, non-alphanumeric stripped — Holded strips separators like dashes) and compared against `HoldedCategoryMap.Tag`. First hit → `MatchSource = Tag`.
  3. **None:** doc lands in the **unmatched bucket** (`MatchStatus = Unmatched`, `MatchSource = None`).
- A **Holded Category Map** row joins a `BudgetCategory` to its dedicated Holded account number/id and its dash-free fallback tag. `IsActive` is the retirement flag, but **nothing retires a row today** — a category deleted in Budget shows as an `Orphan` on the provisioning page and its map row stays active, so `IsActive` is always `true`. Holded accounts are never deleted.
- The **Provisioning page** (`/Finance/HoldedAccounts`) reconciles the live Holded chart-of-accounts against the local `HoldedCategoryMap`: diffs into Mapped / ToAdd / Orphan. "Add one (test)" / "Add all" create accounts in Holded + map rows locally. Additive only.
- The **Holded Sync State** is a singleton row tracking the operational state of the recurring sync job (`Idle / Running / Error`).
- The **Unmatched Queue** (`/Finance/HoldedUnmatched`) is the working surface where the treasurer inspects unattributed docs and triggers a re-sync.
- The **Connector index** (`/Finance/Holded`) is the read-only answer to "what is Finance's half of Holded doing": doc-sync status with its age rendered as a **Stale** badge rather than left implied, the live `HoldedCategoryMap` rows, every pulled `HoldedExpenseDoc` with its match source and raw tags, and the counts that link out to the four working screens. It never calls Holded — cache reads only, so it cannot inherit the live-contacts timeout `/Finance/Creditors` carries (nobodies-collective/Humans#976). Its read model is `IHoldedFinanceAdminService`, section-**internal**, the same shape as the Holded section's `IHoldedAdminService`.

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
| IsApproved | bool? | `false` = still a Holded draft → excluded from actuals. Null = row predates the column and has not been re-synced; treated as not approved. |
| TagsJson | string (jsonb) | Raw tag list from Holded |
| BookedAccountId | string? | First product line's Holded account id |
| BudgetCategoryId | Guid? | Attributed category (null = unmatched) |
| MatchStatus | HoldedMatchStatus | `Matched` or `Unmatched` |
| MatchSource | HoldedMatchSource | `None`, `Account`, or `Tag` |
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
| IsActive | bool | Always `true` today — nothing flips it; see Concepts |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

### HoldedCreditorContact

**Table:** `holded_creditor_contacts`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | The bound member. Bare FK, no nav (cross-section). **Unique** — one binding per member. |
| HoldedContactId | string(64) | Holded's contact id; the stable key for every push and ledger lookup. |
| SupplierAccountNum | int? | The resolved 400000xx. Null until Holded assigns one — see the one-shot resolution in Invariants. Indexed, **deliberately not unique**. |
| Source | CreditorContactSource | `Auto` (created by the first expense push) or `Manual` (bound by an admin). String-converted, max 16. |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

**Cross-section FKs:** `UserId` → `User` (Users) — FK only, no navigation property.

Carries more invariants than any other table here: at most one member per account *and* per Holded
contact, three write paths with different collision remedies, and no unique index to enforce either
— see Invariants for all of it.

### HoldedDocSyncState

**Table:** `holded_doc_sync_state` (singleton, `Id = 1`, lazy-created)

Fields: `LastSyncAt`, `Status` (`Idle / Running / Error` string), `LastError`, `StatusChangedAt`, `LastSyncedDocCount`. Status of the purchase-doc sync only — the ledger mirror (`holded_ledger_lines`, kind-keyed sync states) moved to the **Holded section** (`src/Sections/Humans.Holded/Docs/Holded.md`); Finance reads it via `IHoldedService`.

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

`HoldedDocSyncState.Status` is a plain string (`Idle` / `Running` / `Error`), not an enum — the `HoldedSyncStatus` enum moved to the Holded section with the ledger mirror.

## Routing

Every `/Finance/*` route is gated on `PolicyNames.FinanceAdminOrAdmin`, declared separately on each of the two controllers serving the prefix — this section's `FinanceController` and Budget's `BudgetAdminController`.

### Not this section — the Budget surface on the same prefix

`Humans.Budget`'s `BudgetAdminController` keeps `[Route("Finance")]`, so `/Finance`, `/Finance/Years/*`, `/Finance/Categories/*`, `/Finance/LineItems/*`, `/Finance/CashFlow`, `/Finance/AuditLog` and `/Finance/Admin` are served by Budget. Their table lives in [`Budget.md`](../../Humans.Budget/Docs/Budget.md).

### Holded integration

| Route | Purpose |
|-------|---------|
| `GET /Finance/Holded` | Connector index — doc-sync health with explicit staleness, the live category map, every pulled doc, and the way into the four screens below. Read-only, cache only (nobodies-collective/Humans#1000) |
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

- A purchase doc is attributed **as a whole, by its first product line's** booked account (plus the union of doc-level and line-level tags), and its full `Total` lands on that one category. A multi-line doc booked across several Holded accounts is not split; line-level attribution is a deliberate later refinement (`Service.MapDoc`).
- Actuals are keyed on the **calendar year** of the doc's Europe/Madrid date, matched against `BudgetYear.Year` parsed as an integer (`FinanceController` → `GetActualsForYearAsync` → `HoldedRepository.GetMatchedForYearAsync`). A budget year whose `Year` string is not a plain number, or that does not run January–December, shows no actuals.
- Only `FinanceAdmin` or `Admin` may access any `/Finance/*` route (`[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]` on `FinanceController`).
- `FinanceController` performs no budget mutations at all — its nine actions are the Holded and creditor surface. Budget CRUD on the same prefix is `Humans.Budget`'s `BudgetAdminController`.
- `/Finance/Holded` issues **no** Holded HTTP call. Every figure comes from `holded_doc_sync_state`, `holded_category_map`, `holded_expense_docs` and `holded_creditor_contacts`; the mirror's own health (API budget, ledger sweeps, chart of accounts) is `/Holded`'s and is linked, not restated. Pinned by `GetConnectorOverview_MakesNoHoldedApiCall`.
- The doc sync counts as **stale** at 36 h — the nightly job's 24 h cadence plus half a day of grace — and never having run counts as stale too: budget actuals and the unmatched queue are equally wrong either way, so both raise the same alarm.
- The sync job pulls all purchase docs from Holded each cycle (full-pull). Upsert is keyed on `HoldedDocId`; `CreatedAt` is preserved across re-syncs.
- Full-pull is forced by a Holded API limitation (live probe, 2026-04-26): the purchase-documents endpoint's only date filters filter on `accountingDate`, which is null on most real purchase docs, so there is no reliable incremental-sync key for purchase documents. `ListPurchaseDocumentsAsync` therefore takes no date window at all — it walks `/api/v2/purchases` page by page internally under a 200-page safety cap. Approval state comes from a second full sweep, `ListDraftPurchaseIdsAsync` (`?approval_status=draft`): a doc absent from that set is marked `IsApproved`, so the set must be complete or a draft leaks into the actuals — the client throws rather than dropping an id-less row.
- Attribution runs every sync. Fixing an account mapping or tag in Holded takes effect on next sync or via the manual "Sync Now" button.
- Attribution order: **Account** (booked line account id) → **Tag** (normalized, dash-free) → **Unmatched**. First match wins.
- Tags are normalized: lowercase, all non-alphanumeric characters stripped (Holded strips separators like dashes from tag values).
- Provisioning is additive only, and nothing retires a map entry today: `IsActive` is set `true` on insert and never flipped, so an orphaned row stays active. Holded accounts are never deleted.
- `HoldedExpenseDoc.Total` is included in category-level actuals only when `IsApproved = true` — set on sync as "not in Holded's draft list" (`Service.MapDoc`). Actuals are doc-derived rather than ledger-derived because the budget pages are gross/IVA-inclusive while a 629 balance is net, and ledger lines exist for drafts Holded has not approved.
- Holded API key read from env var `HOLDED_API_KEY_V2` only — never `appsettings.json`.
- The member ↔ creditor-account link resolves through the Holded contact's `supplierRecord.num` field, never by name matching. It is attempted **exactly once**, best-effort, during outbox processing after the payable exists (`ExpenseReportService` → `IHoldedClient.GetContactAsync`); a failure or a null `num` is logged, the null link is stored, and the outbox event is still marked processed so a created doc is never stranded as permanently-failed. **There is no automatic retry** — `SyncCreditorLedgerAsync` imports daybook lines but never re-resolves the contact — so after an initial miss the member stays unlinked until someone runs `POST /Finance/Creditors/Bind`, or a later report from the same member resolves it and backfills the member-level binding (nobodies-collective/Humans#972). `ListCreditorAccountsAsync` returns exactly these unresolved bindings as the `Unresolved` half of its result — they have no account row to sit on, so the account list alone cannot show them — and they render in their own card on `/Finance/Creditors`, making the manual step discoverable rather than silent.
- A 400000xx account — and the Holded contact behind it — binds to **at most one member**. All three write paths test for a conflicting binding (`FindConflictingBinding`, on both the account number and the contact id: after the one-shot number resolution misses, a binding carries a contact id with a null `SupplierAccountNum`, which an account-number-only check cannot see). They differ in the remedy, because only one of them is a guess:
  - **`SetCreditorContactAsync`** (manual bind) — an admin picked the account, so the pick can be wrong: **refuse and write nothing** (nobodies-collective/Humans#974).
  - **`SetCreditorAccountNumAsync`** (automatic, after the push resolves `supplierRecord.num` from a live `GetContactAsync`) — Holded assigned that number to the contact just pushed, so Holded is authoritative and the *older* binding is the wrong guess. Refusing would strand a created payable against a wrong row, so it **writes the truth, logs Error, and leaves the collision standing** on `/Finance/Creditors` for a human (nobodies-collective/Humans#975).
  - The **`seedContactId` / `seedAccountNum` lazy-seed** through **`EnsureCreditorContactAsync`** — this is *our own cache* off the member's prior report, not something Holded just told us, so it is a guess and gets the manual bind's treatment: a seed landing on another member's binding is **refused and dropped**, and the push mints this member their own Holded contact instead. This is also what makes Unbind durable — see below.
- The DB index on `SupplierAccountNum` is deliberately **non-unique**. A unique index would turn a data anomaly into a `DbUpdateException` inside unattended outbox drain — stranding a created Holded doc as permanently-failed — and would have to be created against production rows that may already collide. Enforcement lives in the service (`memory/architecture/db-enforcement-minimal.md`).
- `ListCreditorAccountsAsync` returns **every** binding on an account (`HoldedCreditorAccountRow.Bindings`), not the first, and decides a binding's row **through the Holded contact id**, falling back to the stored `SupplierAccountNum` only for a contact Holded's list does not carry. Which 400000xx a contact holds is Holded's fact, and resolving through it does two jobs. A binding whose number never resolved reaches its row at all — keyed on the number alone the account renders "unbound" while a member in fact holds the contact behind it (the invisibility the #974 second guard's error message ran into), and such a binding could not be unbound from the page. And because the two columns are independent, bindings sharing a contact can carry numbers that disagree; the contact resolution lands them on one row, so the **contact-id half** of the invariant surfaces as a collision instead of two innocent-looking single-member rows. It depends on the live Holded contact list, so it degrades with the names when Holded is unreachable. `/Finance/Creditors` renders each bound member with an **Unbind** button (`POST /Finance/Creditors/Unbind` → `ClearCreditorContactAsync`) and sorts collisions to the top. Unbind removes the whole binding row rather than nulling `SupplierAccountNum`: a binding stripped of its number still carries the other member's Holded contact id, which merges their payables just as thoroughly. The member's next push re-resolves the contact from scratch.
- **Unbind is durable against restoring another member's binding, not against re-deriving the member's own.** Deleting the row is not by itself enough: `ProcessHoldedCreateAsync` seeds the next push from the cleared member's prior report, which still carries whatever contact id and 400000xx were cached on it. The seed-refusal above is what closes that loop — after unbinding a wrong binding, the seed points at the other member's contact, is refused, and the member gets their own new Holded contact. A member's *own* contact still re-derives from their linked history on the next push; that is the documented lazy-seed self-heal and it restores the correct value, not a wrong one.
- **Unbind holds against a push already in flight, in the steady state only.** `ProcessHoldedCreateAsync` spans several Holded calls, so the drain can be mid-push for tens of seconds while an admin clicks Unbind. `EnsureCreditorContactAsync` therefore writes nothing when the member already holds the contact it just PUT and the binding already carries its 400000xx: `UpsertContactAsync` returns the id it was given and `Source`/number come off the binding just read, so the only column that would change is `UpdatedAt`, which nothing reads — and writing it would resurrect a binding the admin cleared, from the copy read before they clicked. Not yet safe in general: a binding still missing its number, and `SetCreditorAccountNumAsync`, write real content and can still lose a concurrent delete (nobodies-collective/Humans#995 — the fix is an update-only repository write, not a version column; see [`no-concurrency-tokens`](../../../../memory/architecture/no-concurrency-tokens.md)).
- Creditor accounts are the `40000000`–`40000999` block (`CreditorAccountMin`/`Max`). Every read that draws on Holded's contact list must filter to it — Holded assigns a supplier number to *every* supplier contact, so an unfiltered list turns ordinary org vendors into bindable member creditor accounts. `SetCreditorContactAsync` validates the posted number against the block server-side — the filtered dropdown is not a gate.
- **Unbound is a valid state, not an error.** A first-time submitter has no creditor contact until their first push; `EnsureCreditorContactAsync` creates it and `SetCreditorAccountNumAsync` records the assigned 400000xx. The bind control exists only for a *pre-existing* Holded contact the auto-create would duplicate. Unbound does **not** imply no contact: `holded_creditor_contacts` was created empty (no backfill), so a member linked before it existed carries a contact id on their older reports only. `ProcessHoldedCreateAsync` therefore seeds `EnsureCreditorContactAsync` from the member's most recent linked report when the report being pushed has no contact id of its own — a null seed makes the client POST a second contact and splits their payables. That push writes the missing binding, so the gap self-heals on first interaction rather than by data migration.
- `ListCreditorAccountsAsync` reads the Holded contact list (`ListContactsAsync`) through a 2-minute `IMemoryCache` entry (design-rules §15 Option A, `CacheKeys.HoldedContacts`) rather than calling Holded live on every load — the 400000xx account **name** lives only in Holded, and the short TTL keeps a contact created today visible without a nightly-cache lag or a per-request call. `ListContactsAsync` itself paginates internally (walks `page` until an empty page returns), so the cached list is never silently truncated. It degrades to blank names when the Holded call fails — transport failure, a rejected key, or an unreadable body — rather than failing the page; unexpected exception types propagate.
- **Never index a nested Holded JSON node directly.** Holded serializes an absent sub-record as an empty *array* (`"supplierRecord": []`), and `JsonNode`'s string indexer throws `InvalidOperationException` on anything that is not a `JsonObject`. Combined with the degrade-to-blank rule above, one such contact blanked every account name on the bind card and `/Finance/Creditors` in production (nobodies-collective/Humans#994). `HoldedClient.ParseContact` reads through `Prop(node, name)`, which yields null for a non-object, and the list parse isolates each contact so one unreadable row costs only its own name. `ListCreditorAccountsAsync` logs when *no* account resolved a name — the all-or-nothing signature of this failure — since it was otherwise silent until a human noticed.

## Negative Access Rules

- Coordinators **cannot** view `/Finance/*` routes.
- The sync job **cannot** delete `HoldedExpenseDoc` rows. Holded-side deletions are not handled in v1.
- Finance **cannot** read or write Budget tables directly, and **cannot write Budget data at all**: its only Budget dependency is the read contract `IBudgetServiceRead`.
- Finance **cannot** write to `holded_expense_docs` outside the sync job. No manual create/edit/delete UI for expense docs in v1.

## Triggers

- None on the budget side: this section only reads Budget, so it fires no Budget-side effects.
- When the sync job starts, `HoldedDocSyncState.Status` flips to `Running`. On success returns to `Idle` with `LastSyncAt` and `LastSyncedDocCount` updated. On exception goes to `Error` with `LastError` populated; next scheduled run retries.

## Cross-Section Dependencies

Derived from `Humans.Finance.csproj`'s project references — four contracts leaves, no section projects:

- **Budget** (`Humans.Budget.Contracts`): `IBudgetServiceRead.GetActiveYearAsync`, for the categories the provisioning plan is built from. Read-only.
- **Holded** (`Humans.Holded.Contracts`): `IHoldedService` for cached ledger lines and account balances, and `IHoldedClient` for the live contact/account calls the provisioning and bind paths make.
- **Users** (`Humans.Users.Contracts`): `IUserServiceRead.GetUserInfosAsync`, to name bound members on `/Finance/Creditors`.
- **GDPR** (`Humans.Gdpr.Contracts`): Finance implements `IUserDataContributor` for the Article 15 export of a member's creditor binding, and for Article 17 erasure — `EraseForUserAsync` drops the binding (`ClearCreditorContactAsync`); `ErasureDeclaration` maps `HoldedCreditorAccount` to `null` (erased in full). The invoices themselves live in Holded and are fiscal records outside this section's ownership.

No Tickets dependency: the cash-flow view that had one is Budget's. Budget never calls into Finance.

## Architecture

**Status:** (A) — Finance has its own service, an owned repository, and an EF migration.
**G5 (own project, `src/Sections/Humans.Finance` + `src/Sections/Humans.Finance.Contracts`) — 2026-08-09.**

**Owning service:** `Service` (`Humans.Finance.Services`), exposed as `IHoldedFinanceService` from the contracts leaf
**Pure matcher:** `HoldedMatcher` (static, no dependencies)
**Owned repository:** `IHoldedRepository` / `Repository` (`Humans.Finance.Data`)  
**Owned tables:** `holded_expense_docs`, `holded_category_map`, `holded_doc_sync_state`, `holded_creditor_contacts`  
**Job:** `HoldedSyncJob` (cron `0 3 * * *`) — **not this section's.** Since G5 lane 4b-2f it is only a shim: its body is `HoldedNightlySync` in `Humans.Holded`, which calls this section's `IHoldedFinanceService.SyncAsync` first and then the ledger mirror. At G5 lane 5b-5 the shim followed its body into `src/Sections/Humans.Holded/Jobs/`; the "Hangfire serializes the declaring type name" claim that had kept it in Base is false — `AddOrUpdate<T>(id, …)` is keyed on the job id.  
**Migrations:** `20260715103643_BaselineFinance` — consolidated onto `FinanceDbContext` (its own history table, `__EFMigrationsHistory_Finance`) when Finance moved off the shared `HumansDbContext` (nobodies-collective/Humans#858); the earlier per-feature migration chain (`HoldedActuals`, `HoldedCreditorData`, `HoldedCreditorContact`, `HoldedLedgerSingleSource`) was squashed into this baseline. Since then: `20260810195350_HoldedExpenseDocIsApproved` (swaps `ApprovedAt` for the nullable `IsApproved` flag) and `20260810204942_HoldedMirrorMovesToHoldedSection` (drops the ledger-mirror tables, which the Holded section now owns)  
**Architecture tests:** `tests/Humans.Finance.Tests/FinanceArchitectureTests.cs`

**Controllers.** `/Finance` is served by two controllers under one route prefix. `Humans.Finance.Controllers.FinanceController` owns the section's own nine actions — `Holded`, `HoldedAccounts`, `HoldedUnmatched`, `Creditors`, `CreditorStatement`, `Bind`, `Unbind`, `Provision`, `HoldedSync/Run`. The other 23 actions on the pre-G5 `FinanceController` were Budget CRUD (years, groups, categories, line items, ticketing projection, cash flow, audit log) and now live in `Humans.Budget.Controllers.BudgetAdminController`, own project since Budget's own G5, keeping `[Route("Finance")]` so no URL moved. See [`Budget.md`](../../Humans.Budget/Docs/Budget.md) for the Budget side of the split.

**The Holded connector is not this section.** `IHoldedClient`, `HoldedClient`, `HoldedClientOptions`, `HoldedApiException` and the connector DTOs belong to the **Holded** section — public on `Humans.Holded.Contracts`, implementation `internal` in `Humans.Holded/Services/` — with their own [`Holded-connector.md`](../../Humans.Holded/Docs/Holded-connector.md) (G5 lane 4b-2f, nobodies-collective/Humans#866). Finance consumes them through that leaf. Consequence for the boundary: `HoldedCreditorLedger.Lines` carries Finance's own `CreditorLedgerLine` rather than the connector's `HoldedLedgerLineDto`, because the contracts leaf may reference only the bottom of the graph and re-exporting another component's wire DTO across a section boundary is the thing the split exists to stop.

**Table names.** `holded_*` tables under a section called `Finance`, and a `FinanceDbContext` naming the live `__EFMigrationsHistory_Finance` table. The mismatch is real and deferred wholesale to nobodies-collective/Humans#1012 — a G5 move changes files, never the schema (design §15 step 10).

**Resources.** No `FinanceResource`. These are English-only finance-admin pages with zero `Localizer` call sites, so the section carves no `.resx`; `_ViewImports` binds `SharedLocalizer` for the first view that needs a string.

> **What exists (Feature 1):**
> - `Controllers/FinanceController.cs` — the Holded/creditor routes. Injects `IHoldedFinanceService` and `IUserServiceRead` only; the Budget-facing actions are `Humans.Budget`'s `BudgetAdminController`.
> - `PolicyNames.FinanceAdminOrAdmin` and `RoleNames.FinanceAdmin` — role + policy wired in `AuthorizationPolicyExtensions.cs`.
> - `Domain/HoldedExpenseDoc.cs`
> - `Domain/HoldedCategoryMap.cs`
> - `Domain/HoldedDocSyncState.cs`
> - `Domain/HoldedMatchStatus.cs`, `HoldedMatchSource.cs`
> - `Services/Service.cs`
> - `Services/IHoldedFinanceAdminService.cs` — `/Finance/Holded`'s read model; **internal**, this section's screen is the only consumer
> - `Models/HoldedConnectorVm.cs` — that screen's view models
> - `Views/Finance/Holded.cshtml`, `SectionAdminNav.cs` — the page and its "Money" sidebar entry
> - `Services/HoldedMatcher.cs`
> - `../Humans.Finance.Contracts/IHoldedFinanceService.cs`
> - `Data/IHoldedRepository.cs`
> - `Data/Repository.cs`
> - `src/Sections/Humans.Holded/Services/HoldedClient.cs`
> - `src/Sections/Humans.Holded/Jobs/HoldedSyncJob.cs`
> - `tests/Humans.Finance.Tests/FinanceArchitectureTests.cs`
> - EF migration `20260525163748_HoldedActuals` for all three Feature 1 Finance-owned tables
>
> **What exists (Feature 2 — ledger single-source):**
> - `Domain/HoldedCreditorContact.cs` — member → 400000xx binding (from #1021)
> - `TotalPaid` / `LastPaymentDate` on `HoldedCreditorStatus` — aggregated straight off the debit lines; no payment row type leaves the service
> - Ledger reads via `IHoldedService` (the mirror moved to the Holded section; sync is `SyncLedgerAsync` there)
> - `IHoldedFinanceService.GetCreditorStatusAsync(int? supplierAccountNum)` / `GetCreditorLedgerAsync(int supplierAccountNum)` — Expenses→Finance read surface, derived from cached lines
> - `IHoldedFinanceService.ListCreditorAccountsAsync` — returns `(Accounts, Unresolved)`; the `Unresolved` half is the bindings with no resolved 400000xx, surfaced on `/Finance/Creditors` for manual bind (nobodies-collective/Humans#972)
> - `IHoldedClient.GetContactAsync`, `ListContactsAsync`, `ListLedgerEntriesAsync`, `UpsertContactAsync` — Holded API surface

### Feature 2 — creditor reads over the Holded section's mirror

The ledger cache and its sync moved to the Holded section (full mirror, all accounts, replace semantics, balance reconciliation — see `src/Sections/Humans.Holded/Docs/Holded.md`). Finance derives creditor status/statements from `IHoldedService.GetLedgerLinesAsync` / `GetAccountBalancesAsync`, range-filtered to the `40000000`–`40000999` creditor block on Finance's side. Sync buttons live on `/Holded`. Page loads still cost **zero Holded calls per view**; the admin creditor overview additionally reads the cached Holded contact list for account names — see Invariants.

The Expenses section reads creditor status via `GetCreditorStatusAsync(supplierAccountNum)` and the statement via `GetCreditorLedgerAsync(supplierAccountNum)`. Both derive from the cached lines: balance = Σdebit − Σcredit (balance ≥ 0 = settled), owed = max(0, −balance), payments = debit lines. The debit lines stay internal to the derivation; only the aggregates (`TotalPaid`, `LastPaymentDate`) leave the service.

**Org-accounting boundary (HARD):** Humans only reads the Holded daybook. It never writes debt-reassignment journal entries or modifies the chart of accounts to reflect internal transfers. `holded_ledger_lines` is a read-through cache of immutable journal facts, not a ledger Humans writes to.

### Owned repository

- **`IHoldedRepository`** — owns `holded_expense_docs`, `holded_category_map`, `holded_doc_sync_state`, `holded_creditor_contacts`
  - No cross-domain navs: `BudgetCategoryId` and `HoldedCreditorContact.UserId` are FK-only, no navigation property
  - Expense docs upsert (full overwrite on re-sync); ledger tables belong to the Holded section

### Current violations

None. Every cross-section call goes through a contracts leaf, and the Budget read-split shipped — `Service` injects `IBudgetServiceRead`, not the full service. No cross-section DbContext reads.

### Touch-and-clean guidance

- **Soft boundary:** `TicketingProjection` and `TicketingBudgetService` are conceptually "actuals materialization" but live in Budget today. Treat as known soft boundary — separate cleanup, not an active violation.
- **Done:** the Budget dependency is `IBudgetServiceRead`; nothing here holds a Budget write surface.
