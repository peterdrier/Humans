<!-- freshness:triggers
  src/Humans.Application/Services/Finance/**
  src/Humans.Application/Interfaces/Finance/**
  src/Humans.Domain/Entities/HoldedTransaction.cs
  src/Humans.Domain/Entities/HoldedSyncState.cs
  src/Humans.Infrastructure/Data/Configurations/Finance/**
  src/Humans.Infrastructure/Repositories/HoldedRepository.cs
  src/Humans.Infrastructure/Services/HoldedClient.cs
  src/Humans.Infrastructure/Jobs/HoldedSyncJob.cs
  src/Humans.Web/Controllers/FinanceController.cs
-->
<!-- freshness:flag-on-change
  FinanceController routes, auth policy (FinanceAdminOrAdmin), or budget-delegation correctness — review when FinanceController or its Budget/Tickets service dependencies change. Holded-specific review guidance lives in the "Planned" sections below; promote it here once Holded services land.
-->

# Finance — Section Invariants

Finance is the **treasurer's reality side** of the money story. Budget owns planning and public presentation; Finance owns actuals, reconciliation, and treasurer-facing operational data. The two share `BudgetGroup` / `BudgetCategory` keys; nothing else.

## Today vs Planned

**Today** (built): `FinanceController` at `/Finance/*` is the treasurer's window over Budget data — Budget years, groups, categories, line items, ticketing projections, audit log, cash-flow view. Gated on `FinanceAdmin` or `Admin`. All reads/writes route through `IBudgetService`, `ITicketingBudgetService`, `ITicketServiceRead`. Finance owns no tables and no Application-layer services yet.

**Planned** (specified, not yet implemented): external-accounting reconciliation via Holded integration — pull purchase docs from Holded each cycle, match to `BudgetCategory` via the `{group-slug}-{category-slug}` tag convention, surface unmatched docs in a treasurer queue, and write resolved tags back to Holded. Concepts, data model, and the four `/Finance/Holded*` routes below describe the **target shape**, not current code.

## Concepts

> All concepts below are **planned** — the Holded integration is specified but not yet implemented as of 2026-05-09. Today's Finance surface uses Budget vocabulary (years, groups, categories, line items).

- A **Holded Transaction** is a purchase invoice pulled from Holded and stored verbatim. Each carries a `MatchStatus` indicating whether it has been resolved to a budget category.
- The **Tag Convention** is `{group-slug}-{category-slug}` — split on the first `-`, group prefix and category suffix resolve against `BudgetGroup.Slug` and `BudgetCategory.Slug` within the budget year covering the doc's date.
- The **Holded Sync State** is a singleton row tracking the operational state of the recurring sync job (`Idle / Running / Error`).
- The **Unmatched Queue** is the working surface where the treasurer resolves docs whose `MatchStatus != Matched`.

## Data Model

> **Not yet built** — no Finance-owned tables exist. The design below is the target schema.

### HoldedTransaction

**Table:** `holded_transactions`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| HoldedDocId | string | Unique. Natural key for upsert. |
| HoldedDocNumber | string | e.g. `F260009` |
| ContactName | string | Vendor name, denormalized. Contacts are not synced as entities. |
| Date | LocalDate | From Holded `date` (epoch s, Europe/Madrid) |
| AccountingDate | LocalDate? | From Holded `accountingDate`; often null |
| DueDate | LocalDate? | From Holded `dueDate` |
| Subtotal | decimal | EUR, raw |
| Tax | decimal | EUR, raw (net of IVA − IRPF — not VAT alone) |
| Total | decimal | EUR, raw |
| PaymentsTotal | decimal | Paid amount |
| PaymentsPending | decimal | Outstanding |
| PaymentsRefunds | decimal | Refunds |
| Currency | string(3) | Lowercase ISO; v1 only handles `eur` |
| ApprovedAt | Instant? | Null = not approved → excluded from totals |
| Tags | jsonb (string[]) | Raw tag list from Holded |
| RawPayload | jsonb | Full Holded JSON for debugging + future field needs |
| SourceIncomingDocId | string? | `from.id` when `from.docType = "incomingdocument"` |
| BudgetCategoryId | Guid? | Matched category (null when unmatched) |
| MatchStatus | HoldedMatchStatus | Persisted to keep the unmatched queue a simple WHERE |
| LastSyncedAt | Instant | Updated every sync that touches this row |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

**Indexes / constraints:**
- Unique on `HoldedDocId`
- Index on `BudgetCategoryId`
- Index on `MatchStatus`
- Index covering period-aggregation queries on `(AccountingDate ?? Date)`

**Cross-section FKs:** `BudgetCategoryId` → `BudgetCategory` (Budget) — **FK only**, no navigation property. `OnDelete: Restrict`.

### HoldedSyncState

**Table:** `holded_sync_states` (singleton, `Id = 1`)

Mirrors `TicketSyncState`. Fields: `LastSyncAt`, `SyncStatus`, `LastError`, `StatusChangedAt`, `LastSyncedDocCount`.

### HoldedMatchStatus

| Value | Int | Description |
|-------|-----|-------------|
| Matched | 0 | Resolved to a `BudgetCategoryId` |
| NoTags | 1 | Doc has empty `tags` array |
| UnknownTag | 2 | At least one tag, none resolve |
| MultiMatchConflict | 3 | 2+ tags resolve to different categories |
| NoBudgetYearForDate | 4 | No `BudgetYear` covers `AccountingDate ?? Date` |
| UnsupportedCurrency | 5 | `currency != "eur"` |

Stored as string via `HasConversion<string>()`.

### HoldedSyncStatus

| Value | Int | Description |
|-------|-----|-------------|
| Idle | 0 | Not currently running |
| Running | 1 | Sync in progress |
| Error | 2 | Last run threw; `LastError` populated |

Stored as string via `HasConversion<string>()`.

## Routing

All routes are gated by `[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]` on `FinanceController`.

### Today — treasurer surface over Budget

| Route | Controller action |
|-------|-------------------|
| `GET /Finance` | `Index` — Budget year overview (active year) |
| `GET /Finance/Years/{id}` | `YearDetail` — Budget year detail |
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

### Planned — Holded integration

| Route | Purpose |
|-------|---------|
| `GET /Finance/HoldedUnmatched` | Unmatched-queue UI |
| `GET /Finance/HoldedTags` | Read-only tag inventory |
| `POST /Finance/HoldedSync/Run` | Manual sync trigger |
| `POST /Finance/HoldedUnmatched/{id}/Assign` | Manual reassignment |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| FinanceAdmin, Admin | Full access to all `/Finance/*` routes. View budget data, manage years/groups/categories/line items, trigger ticketing sync. Will gain: view transactions, trigger Holded sync, reassign unmatched docs (when Holded integration lands). |
| Department coordinator | None — Finance routes are FinanceAdmin-only. |
| Any other authenticated human | None |

## Invariants

> Most invariants below are **target invariants** for the Holded integration. The two marked "current" apply today.

- **(current)** Only `FinanceAdmin` or `Admin` may access any `/Finance/*` route (`[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]` on `FinanceController`).
- **(current)** All budget mutations in `FinanceController` route through `IBudgetService` — the controller owns no Finance-domain tables.
- *(target)* The sync job pulls all purchase docs from Holded each cycle (full-pull, no incremental). Strategy is forced by the Holded API's `?starttmp=&endtmp=` filter operating on `accountingDate`, which is null on real docs.
- *(target)* Upsert is keyed on `HoldedDocId`. `CreatedAt` is preserved across re-syncs; every other field is overwritten.
- *(target)* Match resolution runs every sync — it is not persisted incrementally beyond the `MatchStatus` value itself. Fixing tags in Holded (or via the unmatched-queue UI) takes effect on next sync.
- *(target)* `MatchStatus` is determined by ordered rules (UnsupportedCurrency → NoBudgetYearForDate → NoTags → UnknownTag → MultiMatchConflict → Matched); first failure wins.
- *(target)* Tags are computed as `{group-slug}-{category-slug}`; no separate tag-mapping table exists. Slug fields on `BudgetGroup` and `BudgetCategory` are the single source of truth.
- *(target)* Manual reassignment writes the corrected tag back to Holded via `PUT /api/invoicing/v1/documents/purchase/{id}`. If the PUT fails, the local match is still saved and a warning is surfaced; treasurer is expected to fix tags in Holded directly.
- *(target)* Every manual reassignment writes an `AuditLogEntry` via `IAuditLogService` recording actor, timestamp, doc id, and category id.
- *(target)* Holded API key is read from env var `HOLDED_API_KEY` only — never `appsettings.json`.
- *(target)* `HoldedTransaction.Total` is included in category-level "actual" sums only when `ApprovedAt IS NOT NULL`.

## Negative Access Rules

- Coordinators **cannot** view `/Finance/*` routes.
- *(target)* The sync job **cannot** delete `HoldedTransaction` rows. Holded-side deletions are not handled in v1; treasurer cleans up manually.
- *(target)* Finance **cannot** read or write Budget tables directly — all cross-section access goes through `IBudgetService`.
- *(target)* Finance **cannot** write to `holded_transactions` outside the sync job and `ReassignAsync`. No manual create/edit/delete UI for transactions in v1.

## Triggers

- **(current)** None in the Finance domain layer. Budget mutations via `FinanceController` trigger Budget-section side effects (audit log entries written by `IBudgetService`).
- *(target)* When a manual reassignment succeeds, an `AuditLogEntry` is written and (best-effort) a `PUT` request is made to Holded to add the corrected tag to the doc.
- *(target)* When the sync job starts, `HoldedSyncState.SyncStatus` flips to `Running`. On success returns to `Idle` with `LastSyncAt` and `LastSyncedDocCount` updated. On exception goes to `Error` with `LastError` populated; next scheduled run retries.

## Cross-Section Dependencies

- **(current)** **Budget:** `IBudgetService` (read + write — all budget year/group/category/line-item mutations in `FinanceController` route through it), `ITicketingBudgetService` (ticketing projection and actuals sync).
- **(current)** **Tickets:** `ITicketServiceRead.GetTicketOrdersAsync` (cash flow view derives gross paid revenue from `TicketOrderInfo`).
- *(target)* **Budget:** `IBudgetService.GetCategoryBySlugAsync`, `GetYearForDateAsync`, `GetCategoriesByYearAsync`, `GetTagInventoryAsync` — all read-only from the Holded sync path.
- *(target)* **Audit Log:** `IAuditLogService.LogAsync` — manual-reassignment audit trail.

Budget never calls into Finance.

## Architecture

**Owning services:** `HoldedSyncService`, `HoldedTransactionService` (not yet built)
**Owned tables:** `holded_transactions`, `holded_sync_states` (not yet built — no EF migration exists)
**Status:** (C) Pre-migration — `FinanceController` exists as a Budget-backed UI shell; Finance-domain services, entities, repository, and job have not been implemented.

> **What currently exists:**
> - `src/Humans.Web/Controllers/FinanceController.cs` — Budget admin + treasurer view. Injects `IBudgetService`, `ITicketingBudgetService`, `ITicketServiceRead`. Zero Finance-owned infrastructure.
> - `PolicyNames.FinanceAdminOrAdmin` and `RoleNames.FinanceAdmin` — role + policy wired in `AuthorizationPolicyExtensions.cs`.
>
> **What does not yet exist:**
> - `src/Humans.Domain/Entities/HoldedTransaction.cs`
> - `src/Humans.Domain/Entities/HoldedSyncState.cs`
> - `src/Humans.Application/Services/Finance/` (any file)
> - `src/Humans.Application/Interfaces/Finance/` (any file)
> - `src/Humans.Infrastructure/Repositories/HoldedRepository.cs`
> - `src/Humans.Infrastructure/Services/HoldedClient.cs`
> - `src/Humans.Infrastructure/Jobs/HoldedSyncJob.cs`
> - `tests/Humans.Application.Tests/Architecture/FinanceArchitectureTests.cs`
> - Any EF migration for `holded_transactions` or `holded_sync_states`

### For (C) Pre-migration sections

> **Status (pre-migration):** Finance-domain services do not yet exist. When the Holded integration is built, it must start in `Humans.Application` per design-rules §15h(1) (new sections start as (A)). **Delete this block once the migration lands and Finance services live in `Humans.Application.Services.Finance/` with `HoldedRepository.cs` in `Humans.Infrastructure/Repositories/`.**

#### Target repositories

- **`IHoldedRepository`** — owns `holded_transactions`, `holded_sync_states`
  - No cross-domain navs: `BudgetCategoryId` is FK-only, no navigation property
  - No append-only constraint: transactions are upserted (full overwrite on re-sync)

#### Current violations

None applicable — Finance owns no tables yet. The `FinanceController` is correctly calling Budget/Tickets via their service interfaces; no cross-section DbContext reads.

#### Touch-and-clean guidance

- When the Holded integration lands: add `FinanceArchitectureTests.cs` pinning no-EF-import-in-service, no cross-section repository injection, no direct `BudgetCategory` table access.
- When the Holded integration lands: update design-rules §15 migration status list to record Finance as migrated (A).
- **Soft boundary:** `TicketingProjection` and `TicketingBudgetService` are conceptually "actuals materialization" but live in Budget today. Treat as known soft boundary — separate cleanup, not an active violation.
