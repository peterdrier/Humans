# Holded — Data Access

## Holded

Folder: `src/Sections/Humans.Holded/Services/` (namespace
`Humans.Holded.Services`; class is literally named `Service`, matching
the single-service convention several other sections use). **DbContext:**
`HoldedDbContext`. `Repository`
(`src/Sections/Humans.Holded/Data/Repository.cs`, implements
`IHoldedMirrorRepository`) injects `IDbContextFactory<HoldedDbContext>`
directly. Owns `HoldedLedgerLines`,
`HoldedSyncStates`, `HoldedAccounts`, `HoldedApiCalls`, `HoldedPurchaseDocs`,
`HoldedBankMovements`.

This is the **ledger mirror**: it sweeps Holded's daybook journal into
`HoldedLedgerLines` with replace semantics (a fetched window is the
truth for that window — deletions and reclassifications on Holded's side
are reflected, not just appended), refreshes the chart-of-accounts cache
(`HoldedAccounts`), reconciles per-account balances against Holded on
every sync (a drifted account gets one targeted full-history re-pull,
capped per run and rotated by day), and drains the connector's
API-call log (`HoldedApiCalls`, metering against `HoldedSectionOptions.MonthlyCallBudget`).
All cross-section reads of ledger data go through Finance (creditor
balances and statements, which Expenses reads in turn) and are served from
this cache — no per-page Holded API calls.

### HoldedService (Scoped — class name `Service`, implements `IHoldedService` + `IHoldedAdminService`)

Repository: `IHoldedMirrorRepository`.

| Table | R/W |
|-------|-----|
| HoldedLedgerLines | R/W (replace-window upsert via `SyncLedgerAsync` — full history on cold cache or `full: true`, else a 45-day trailing window; one account's full history via `SyncAccountLedgerAsync`; read by account (optional window and type), by entry numbers, and as per-account / per-account-and-type / windowed sums grouped in SQL — never the whole table) |
| HoldedSyncStates | R/W (one row per `HoldedSyncKind`: Ledger, FullSync, Accounts, PurchaseDocs, Contacts, BankMovements) |
| HoldedBankMovements | R/W (window replace via `SyncBankMovementsAsync`; read by treasury and date window by `GetBankMovementsAsync`) |
| HoldedPurchaseDocs | R/W (full replace via `SyncPurchaseDocumentsAsync`, an unfetched note keeps the stored one; named documents' notes re-read by `SyncPurchaseDocumentNotesAsync`; read whole by `GetPurchaseDocumentsAsync`, which Finance attributes on read) |
| HoldedContacts | R/W (full replace via `SyncContactsAsync`; read whole by `GetContactsAsync`, null until the first sync) |
| HoldedAccounts | R/W (chart-of-accounts cache, refreshed and reconciled every ledger sync, or alone by `SyncChartAsync`) |
| HoldedApiCalls | R/W (drained from `IHoldedCallLog` after each sync/overview read) |

No cross-section service calls — `IHoldedClient` (this section's own connector, see
[`Holded-connector.md`](Holded-connector.md)) and `IHoldedCallLog`
(section-internal in-process call-log buffer drained into `HoldedApiCalls`)
are its only outbound
dependencies, plus `IOptions<HoldedSectionOptions>` for the monthly
call-budget display. Implements `IHoldedService` (the ledger-read
surface consumed cross-section by Finance's `Service` —
`GetLedgerLinesAsync`, `GetAccountBalancesAsync`; also `IsConfigured`,
`GetEntryAsync`,
`GetSumsByAccountAsync`, `GetChartAsync`, `SyncChartAsync`,
`GetPurchaseDocumentsAsync`, `SyncPurchaseDocumentsAsync`, `SyncPurchaseDocumentNotesAsync`, `GetContactsAsync` and `SyncContactsAsync`
for Finance, Expenses and Store) and `IHoldedAdminService` (the `/Holded` pages — summary,
accounts, calls by month, account statements, entries). No
`IMemoryCache`. In-process `SemaphoreSlim` gates serialize
`SyncLedgerAsync`, `SyncPurchaseDocumentsAsync` and `SyncContactsAsync` runs (the nightly Hangfire job and an admin-triggered
sync can otherwise race the replace-window).

### HoldedWrite (Scoped — implements `IHoldedWrite`)

Repository: `IHoldedMirrorRepository`. Reads `HoldedAccounts` (an account's Holded id by number, for
purchase and sales lines). Calls `IHoldedClient` for every lookup and write, and `IHoldedService`
to refresh a mirror after a write: `SyncChartAsync` after creating an expense account,
`SyncContactsAsync` after a contact create or delete, `SyncLedgerAsync` after a matched delete. No
other tables.

---


