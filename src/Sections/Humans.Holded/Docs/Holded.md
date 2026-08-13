<!-- freshness:triggers
  src/Sections/Humans.Holded/**
  src/Sections/Humans.Holded.Contracts/**
  src/Humans.Application/Interfaces/Holded/**
  src/Humans.Infrastructure/Services/Holded/**
  src/Humans.Infrastructure/Jobs/HoldedSyncJob.cs
  src/Humans.Infrastructure/Jobs/HoldedExpenseOutboxJob.cs
-->

# Holded (section) — Section Invariants

The **ledger mirror**: a local, re-derivable copy of Holded's daybook and chart of accounts,
plus the sync that maintains it and the `/Holded` admin screen. Every cross-section ledger
read is served from this cache — zero Holded calls per page view. The HTTP connector itself
stays in Base ([`docs/sections/Holded.md`](../../../../docs/sections/Holded.md)).

## Concepts

- **Mirror, not source**: every table is reconstructible from the Holded API. Dropping and
  resyncing is always safe; no data migrations ever.
- **Replace semantics**: a sweep is the truth for its window — cached lines the sweep no longer
  returns are deleted (an empty fetch still deletes). Append-only caching was the phantom-row
  bug (deleted/reclassified lines lingering forever).
- **Reconciliation**: after every sweep, each non-archived account's chart balance is compared
  to the local ledger sum; drifted accounts get one targeted full-history re-pull (capped at 10
  per run, logged when capped). Residual mismatches are **reportable state** on the sync row,
  never a failure — Holded's own chart totals can exclude unconfirmed entries.

## Data Model (`HoldedDbContext`, history `__EFMigrationsHistory_Holded`)

| Table | Content |
|---|---|
| `holded_ledger_lines` | full daybook mirror, all accounts, unique `(EntryNumber, Line)` |
| `holded_accounts` | chart-of-accounts cache with Holded's debit/credit/balance totals |
| `holded_api_calls` | per-call metering (endpoint, status, rate-limit headers) |
| `holded_sync_states` | one row per sync kind (`Ledger`, `Accounts`, `FullSync`), lazy-created |

Sentinel table is `holded_accounts` — created by this baseline alone, so its existence proves
the baseline ran. (`holded_ledger_lines` could not carry that proof: the historical chain and
pre-split Finance both created it.) Sections migrate in name order, so Finance's drop of its
mirror tables runs before this baseline recreates them.

## Routing

- `/Holded` — admin screen: usage meter (API's number displayed; budget = config
  `Holded:MonthlyCallBudget`, default 2000), calls-by-month, per-kind sync states plus
  Finance's doc-sync row, Sync now / Full sync buttons, and the chart of accounts with
  reconciliation flags — split by PGC group (named in English from the account number's leading
  digit, not Holded's Spanish `group` string), accounts with no cached lines hidden by default.
- `/Holded/Accounts/{number}` — the general-ledger page for ANY account (departments, banks,
  creditors), native Holded sign, header + all cached journal lines.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Finance admin / Admin | Everything above (`PolicyNames.FinanceAdminOrAdmin`). |
| Other sections | Read via `IHoldedService`; trigger sync (nightly job). |
| Any human | None directly. |

## Invariants

- Nightly sweep = one trailing **45-day** window anchored on *now* (accounting-date filter ⇒
  never anchor on the newest cached line); full sweep (inception → today) on cold cache or the
  Full sync button. Reconciliation catches anything older, so correctness does not depend on
  window width — the window is a quota choice.
- Sweeps are serialized by a non-blocking in-process gate; a second caller is skipped and told
  so, never queued (single-server deployment).
- Reads (`GetLedgerLinesAsync`, `GetAccountBalancesAsync`) never call Holded.
- No user-scoped data → no GDPR contributor. The member→creditor binding lives in Finance.

## Negative Access Rules

- Only this section's repository touches `HoldedDbContext`.
- This section never reads Finance's tables; the admin screen obtains Finance's doc-sync row
  via `IHoldedFinanceService` in the controller.

## Triggers

- `HoldedSyncJob` (nightly, Base): Finance's doc sync, then `SyncLedgerAsync(full: false)`.
- `/Holded` buttons: `SyncNow` (incremental + reconcile), `FullSync`.

## Cross-Section Dependencies

- Outbound: `IHoldedClient` (Base connector) only.
- Inbound: Finance (creditor statuses/statements/actuals via `IHoldedService`), the nightly job.

## Architecture

**Owning section:** `Holded` (`src/Sections/Humans.Holded`, G5)
**Public contract:** `Humans.Holded.Contracts.IHoldedService` (+ `HoldedLedgerLineInfo`)
**Owned tables:** the four above
**Status:** (G5) Own project. Spec: [`2026-08-10-holded-v2-migration-design.md`](2026-08-10-holded-v2-migration-design.md).
