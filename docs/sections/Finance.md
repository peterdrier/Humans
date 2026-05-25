<!-- freshness:triggers
  src/Humans.Application/Services/Finance/**
  src/Humans.Application/Interfaces/Finance/**
  src/Humans.Domain/Entities/HoldedExpenseDoc.cs
  src/Humans.Domain/Entities/HoldedCategoryMap.cs
  src/Humans.Domain/Entities/HoldedSyncState.cs
  src/Humans.Domain/Entities/HoldedCreditorBalance.cs
  src/Humans.Domain/Entities/HoldedPayment.cs
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

**Today — Holded creditor-data cache** (built, Feature 2): nightly sync of creditor balances and payment rows from Holded; `GetCreditorStatusAsync` exposes the read surface to Expenses for paid-detection. See [Feature 2](#feature-2--holded-creditor-data-cache) below.

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

### HoldedCreditorBalance

**Table:** `holded_creditor_balances`

Cached chartofaccounts row for a 400000xx supplier account. Keyed by `SupplierAccountNum` (unique). `Balance` is signed; negative = org owes the creditor. Refreshed nightly by `SyncCreditorDataAsync`. `LastSyncedAt` updated on every upsert.

### HoldedPayment

**Table:** `holded_payments`

Cached Holded payment row, keyed by `HoldedPaymentId` (unique). Indexed by `HoldedContactId` for fast per-supplier look-up. Refreshed nightly by `SyncCreditorDataAsync`.

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
| `POST /Finance/HoldedAccounts/Provision` | Add one or all pending Holded accounts + map rows |
| `POST /Finance/HoldedSync/Run` | Manual sync trigger |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| FinanceAdmin, Admin | Full access to all `/Finance/*` routes. View budget data, manage years/groups/categories/line items, trigger ticketing sync. Provision Holded accounts, trigger Holded sync, inspect unmatched docs. |
| Department coordinator | None — Finance routes are FinanceAdmin-only. |
| Any other authenticated human | None |

## Invariants

- Only `FinanceAdmin` or `Admin` may access any `/Finance/*` route (`[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]` on `FinanceController`).
- All budget mutations in `FinanceController` route through `IBudgetService` — the controller owns no Finance-domain tables beyond the Holded integration.
- The sync job pulls all purchase docs from Holded each cycle (full-pull). Upsert is keyed on `HoldedDocId`; `CreatedAt` is preserved across re-syncs.
- Attribution runs every sync. Fixing an account mapping or tag in Holded takes effect on next sync or via the manual "Sync Now" button.
- Attribution order: **Account** (booked line account id) → **Tag** (normalized, dash-free) → **Unmatched**. First match wins.
- Tags are normalized: lowercase, all non-alphanumeric characters stripped (Holded strips separators like dashes from tag values).
- Provisioning is additive only. Retiring a map entry sets `IsActive = false`; it does not delete the Holded account.
- `HoldedExpenseDoc.Total` is included in category-level actuals only when `ApprovedAt IS NOT NULL`.
- Holded API key read from env var `HOLDED_API_KEY` only — never `appsettings.json`.

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
**Owned tables:** `holded_expense_docs`, `holded_category_map`, `holded_sync_states`, `holded_creditor_balances`, `holded_payments`  
**Job:** `HoldedSyncJob` (cron `0 3 * * *`)  
**Migrations:** `20260525163748_HoldedActuals` (F1), `20260525_HoldedCreditorData` (F2)  
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
> **What exists (Feature 2):**
> - `src/Humans.Domain/Entities/HoldedCreditorBalance.cs`
> - `src/Humans.Domain/Entities/HoldedPayment.cs`
> - `IHoldedRepository.UpsertCreditorBalancesAsync`, `GetCreditorBalanceByAccountNumAsync`, `UpsertPaymentsAsync`, `GetPaymentsByContactAsync`
> - `IHoldedFinanceService.SyncCreditorDataAsync` — nightly cache refresh (called from `HoldedSyncJob`)
> - `IHoldedFinanceService.GetCreditorStatusAsync(int? supplierAccountNum, string holdedContactId)` — Expenses→Finance read surface
> - `IHoldedClient.GetContactAsync`, `ListChartOfAccountsAsync`, `ListPaymentsAsync`, `UpsertContactAsync` — extended Holded API surface

### Feature 2 — Holded creditor-data cache

`SyncCreditorDataAsync` runs nightly as part of `HoldedSyncJob`. It pulls the chartofaccounts and payments from Holded and upserts them into `holded_creditor_balances` and `holded_payments` respectively. The Expenses section reads creditor status via `GetCreditorStatusAsync(supplierAccountNum, holdedContactId)`: it checks the cached balance (balance ≥ 0 means settled) and falls back to the payments cache for the contact.

**Org-accounting boundary (HARD):** Humans only reads Holded balances. It never writes debt-reassignment journal entries or modifies the chartofaccounts to reflect internal transfers. The `holded_creditor_balances` table is a read-through cache, not a ledger.

### Owned repository

- **`IHoldedRepository`** — owns `holded_expense_docs`, `holded_category_map`, `holded_sync_states`, `holded_creditor_balances`, `holded_payments`
  - No cross-domain navs: `BudgetCategoryId` is FK-only, no navigation property
  - No append-only constraint: expense docs and creditor rows are upserted (full overwrite on re-sync)

### Current violations

None. `FinanceController` calls Budget/Tickets via their service interfaces. `HoldedFinanceService` calls `IBudgetService` for cross-section reads (acknowledged tech debt; future read-split to `IBudgetServiceRead` noted). No cross-section DbContext reads.

### Touch-and-clean guidance

- **Soft boundary:** `TicketingProjection` and `TicketingBudgetService` are conceptually "actuals materialization" but live in Budget today. Treat as known soft boundary — separate cleanup, not an active violation.
- **Future:** split Holded's `IBudgetService` dependency to `IBudgetServiceRead` and introduce `IBudgetServiceRead` where only reads are needed.
