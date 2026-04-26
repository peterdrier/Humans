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
  Holded sync, match-status semantics, manual-reassignment flow, or sync-state singleton may have changed — review when Finance services/entities/job/auth change.
-->

# Finance — Section Invariants

External-accounting reconciliation: pulls Holded purchase invoices into Humans, matches them to budget categories via tag, and surfaces planned-vs-actual rolls-up to the treasurer.

> Finance is the **reality side** of the money story. Budget owns planning and public presentation; Finance owns actuals, reconciliation, and treasurer-facing operational data. They share the `BudgetGroup` / `BudgetCategory` keys but nothing else.

## Concepts

- A **Holded Transaction** is a purchase invoice pulled from Holded and stored verbatim. Each carries a `MatchStatus` indicating whether it has been resolved to a budget category.
- The **Tag Convention** is `{group-slug}-{category-slug}` — split on the first `-`, group prefix and category suffix resolve against `BudgetGroup.Slug` and `BudgetCategory.Slug` within the budget year covering the doc's date.
- The **Holded Sync State** is a singleton row tracking the operational state of the recurring sync job (`Idle / Running / Error`).
- The **Unmatched Queue** is the working surface where the treasurer resolves docs whose `MatchStatus != Matched`.

## Data Model

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

| Route | Purpose |
|-------|---------|
| `/Finance` | Existing year-detail accordion, gains an "Actual" column and per-category Holded drill-down |
| `/Finance/HoldedUnmatched` | Unmatched-queue UI |
| `/Finance/HoldedTags` | Read-only tag inventory |
| `POST /Finance/HoldedSync/Run` | Manual "Sync Now" trigger |
| `POST /Finance/HoldedUnmatched/{id}/Assign` | Manual reassignment |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| FinanceAdmin, Admin | Full access to all Finance routes. View transactions, trigger sync, reassign unmatched docs. |
| Department coordinator | None — actuals are FinanceAdmin-only in v1. May gain per-team read access in a later iteration. |
| Any other authenticated human | None |

## Invariants

- The sync job pulls all purchase docs from Holded each cycle (full-pull, no incremental). Strategy is forced by the Holded API's `?starttmp=&endtmp=` filter operating on `accountingDate`, which is null on real docs.
- Upsert is keyed on `HoldedDocId`. `CreatedAt` is preserved across re-syncs; every other field is overwritten.
- Match resolution runs every sync — it is not persisted incrementally beyond the `MatchStatus` value itself. Fixing tags in Holded (or via the unmatched-queue UI) takes effect on next sync.
- `MatchStatus` is determined by ordered rules (UnsupportedCurrency → NoBudgetYearForDate → NoTags → UnknownTag → MultiMatchConflict → Matched); first failure wins.
- Tags are computed as `{group-slug}-{category-slug}`; **no separate tag-mapping table exists**. Slug fields on `BudgetGroup` and `BudgetCategory` are the single source of truth.
- Manual reassignment writes the corrected tag back to Holded via `PUT /api/invoicing/v1/documents/purchase/{id}`. If the PUT fails, the local match is still saved and a warning is surfaced; treasurer is expected to fix tags in Holded directly.
- Every manual reassignment writes an `AuditLogEntry` via `IAuditLogService` recording actor, timestamp, doc id, and category id.
- Holded API key is read from env var `HOLDED_API_KEY` only — never `appsettings.json`.
- `HoldedTransaction.Total` is included in category-level "actual" sums **only when `ApprovedAt IS NOT NULL`**.

## Negative Access Rules

- Coordinators **cannot** view `/Finance/*` routes — actuals are FinanceAdmin-only in v1.
- The sync job **cannot** delete `HoldedTransaction` rows. Holded-side deletions are not yet handled in v1; treasurer cleans up manually.
- Finance **cannot** read or write Budget tables directly — all cross-section access goes through `IBudgetService`.
- Finance **cannot** write to `holded_transactions` outside the sync job and `ReassignAsync`. There is no manual create/edit/delete UI for transactions in v1.

## Triggers

- When a manual reassignment succeeds, an `AuditLogEntry` is written and (best-effort) a `PUT` request is made to Holded to add the corrected tag to the doc.
- When the sync job starts, `HoldedSyncState.SyncStatus` flips to `Running`. On success it returns to `Idle` with `LastSyncAt` and `LastSyncedDocCount` updated. On exception it goes to `Error` with `LastError` populated; the next scheduled run retries.

## Cross-Section Dependencies

- **Budget:** `IBudgetService.GetCategoryBySlugAsync(year, groupSlug, categorySlug)`, `IBudgetService.GetYearForDateAsync(date)`, `IBudgetService.GetCategoriesByYearAsync(yearId)`, `IBudgetService.GetTagInventoryAsync(yearId)` — all read-only.
- **Audit Log:** `IAuditLogService.LogAsync(...)` — manual-reassignment audit trail.

Budget never calls into Finance.

## Architecture

**Owning services:** `HoldedSyncService`, `HoldedTransactionService`
**Owned tables:** `holded_transactions`, `holded_sync_states`
**Status:** (A) Migrated — new section, born under design-rules §15h(1) (new code starts in `Humans.Application` with a repository).

- Services live in `Humans.Application.Services.Finance/` and never import `Microsoft.EntityFrameworkCore`.
- `IHoldedRepository` (impl `Humans.Infrastructure/Repositories/HoldedRepository.cs`, §15b Singleton + `IDbContextFactory`) is the only file that touches Finance tables via `DbContext`.
- `IHoldedClient` (impl `Humans.Infrastructure/Services/HoldedClient.cs`) is a typed `HttpClient` wrapper. API key is bound from env var `HOLDED_API_KEY`; never logged.
- `HoldedSyncJob` (`Humans.Infrastructure/Jobs/HoldedSyncJob.cs`) is a Hangfire recurring job at `0 30 4 * * *` UTC.
- **Decorator decision — no caching decorator.** Finance is FinanceAdmin-only and low-traffic. Same rationale as Budget / Governance.
- **Cross-domain navs:** none. `BudgetCategoryId` is FK-only with no navigation property; lookups go through `IBudgetService`.
- **Cross-section calls** route through `IBudgetService` (read-only) and `IAuditLogService` (write-only).
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/FinanceArchitectureTests.cs` pins the shape (no EF Core import in service, no cross-section repositories injected, no direct `BudgetCategory` table access).
- **GDPR** — no `IUserDataContributor`. Finance owns no per-user data; vendor identity is a Holded contact, not a Humans user. If per-user spend ever lands here, revisit per design-rules §8a.

### Soft-boundary note

`TicketingProjection` and `TicketingBudgetSyncJob` are conceptually "actuals materialization" but live in Budget today (per V1d). They are not migrated to Finance as part of this work. Treat that as a known soft boundary — separate cleanup, not an active violation.
