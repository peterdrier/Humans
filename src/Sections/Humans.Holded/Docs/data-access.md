# Holded — Data Access

## Holded

Folder: `src/Sections/Humans.Holded/Services/` (namespace
`Humans.Holded.Services`; class is literally named `Service`, matching
the single-service convention several other sections use). **DbContext:**
`HoldedDbContext`. `Repository`
(`src/Sections/Humans.Holded/Data/Repository.cs`, implements
`IHoldedMirrorRepository`) injects `IDbContextFactory<HoldedDbContext>`
directly. Owns `HoldedLedgerLines`,
`HoldedSyncStates`, `HoldedAccounts`, `HoldedApiCalls`.

This is the **ledger mirror**: it sweeps Holded's daybook journal into
`HoldedLedgerLines` with replace semantics (a fetched window is the
truth for that window — deletions and reclassifications on Holded's side
are reflected, not just appended), refreshes the chart-of-accounts cache
(`HoldedAccounts`), reconciles per-account balances against Holded on
every sync (a drifted account gets one targeted full-history re-pull,
capped per run and rotated by day), and drains the connector's
API-call log (`HoldedApiCalls`, metering against `HoldedSectionOptions.MonthlyCallBudget`).
All cross-section reads of ledger data (Finance's creditor balances,
Expenses' creditor status) are served from this cache — no per-page
Holded API calls.

### HoldedService (Scoped — class name `Service`, implements `IHoldedService` + `IHoldedAdminService`)

Repository: `IHoldedMirrorRepository`.

| Table | R/W |
|-------|-----|
| HoldedLedgerLines | R/W (replace-window upsert via `SyncLedgerAsync` — full history on cold cache or `full: true`, else a 45-day trailing window; read for balances/statement/ledger lookups) |
| HoldedSyncStates | R/W (one row per `HoldedSyncKind`: Ledger, FullSync, Accounts) |
| HoldedAccounts | R/W (chart-of-accounts cache, refreshed and reconciled every sync) |
| HoldedApiCalls | R/W (drained from `IHoldedCallLog` after each sync/overview read) |

No cross-section service calls — `IHoldedClient` (this section's own leaf,
`Humans.Holded.Contracts` — the Holded API connector; its only journal-affecting
write is `PayPurchaseDocumentAsync`, `POST /purchases/{id}/payments`, called by
Finance when a SEPA transfer is booked) and `IHoldedCallLog`
(section-internal in-process call-log buffer drained into `HoldedApiCalls`)
are its only outbound
dependencies, plus `IOptions<HoldedSectionOptions>` for the monthly
call-budget display. Implements `IHoldedService` (the ledger-read
surface consumed cross-section by `HoldedFinanceService` —
`GetLedgerLinesAsync`, `GetAccountBalancesAsync`) and `IHoldedAdminService`
(the `/Holded` admin-overview surface — usage, monthly call counts,
per-account reconciliation status, account statements). No
`IMemoryCache`. A single in-process `SemaphoreSlim` gate serializes
`SyncLedgerAsync` runs (the nightly Hangfire job and an admin-triggered
sync can otherwise race the replace-window).

---


