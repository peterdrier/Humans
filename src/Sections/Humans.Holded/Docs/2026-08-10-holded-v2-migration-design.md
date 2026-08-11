# Holded API v2 migration + full ledger mirror + Holded admin screen — design

**Status:** Draft — awaiting Peter's sign-off
**Date:** 2026-08-10
**Supersedes:** the sync mechanics of `2026-06-15-holded-ledger-single-source-design.md` (its "daybook is the single source, cache it, derive everything" decision stands; the v1 `dailyledger` transport and creditor-range scoping do not).

## Context

Holded launched API v2 (June 2026): REST/JSON at `https://api.holded.com/api/v2`, Bearer auth, cursor pagination. v1 is deprecated. Facts confirmed against the live account with a read-only token (not doc excerpts):

- `GET /usage` → `{"type":"automation_token","period":"2026-08","usage":36,"limit":2000000,"count":1,"secondary_usages":{"api_v1_legacy_1":35,...}}`. **The 2,000,000 `limit` is Holded's billable-overage ceiling, not the free allowance** (Peter, 2026-08-10) — the real budget is the plan tier (~2,000/month Basic, 7,500 Standard), and overage costs money. Design target: nightly sync ≤5 calls; budget tracked against config `Holded:MonthlyCallBudget` (default 2000), with the API's number displayed but not budgeted against.
- `GET /accounting-accounts?include_empty=false` → 267 active accounts; each item: `id, color, number, name, description, group, debit, credit, balance` (decimals as strings), `archived, non_deductible`. Optional `start_date`/`end_date` scope the totals; omitted = all-time. **Per-account all-time debit/credit/balance in one call** — this powers reconciliation.
- `GET /ledger-entries?start_date=&end_date=[&account=][&limit=≤200][&cursor=]` → `{items[], cursor, has_more}`; item: `entry_number, line, date, type, description, doc_description, account, debit, credit` (strings), `tags[], checked`. **Dates are `DD/MM/YYYY`**, not ISO. No `updated_since` filter. A 2.5-year range worked in one call (no v1-style 1-year window cap observed); chunk by year only if the API starts rejecting ranges.
- v2 equivalents exist for every v1 call we make: `purchases` CRUD + `/attachments` (+ `/approve`, `/payments`), `contacts`, `expenses-accounts`, plus `webhooks` management.
- Webhooks (HMAC-SHA256-signed, retried with backoff) cover purchases/payments/contacts — **no events for raw ledger entries**, so they can't replace ledger polling. **Deferred** (decision below).
- 429 handling: `Retry-After` header must be honored; `X-RateLimit-*` headers identify the tripped window (`minute` or `month`).

## Decisions (Peter, 2026-08-10)

1. v2 migration now; **v1 code deleted in the same PR** — no dual-path period.
2. Webhooks: later. Nightly polling is 2–3 calls/night.
3. Ledger mirror widens to **all accounts, full history** (was: creditor-block only; #1241 widened that block to `40000000–40000999`).
4. Admin screen gets a **Full sync** button alongside incremental sync.
5. **Holded becomes its own vertical section** — `src/Sections/Humans.Holded` + `Humans.Holded.Contracts` — owning the external-system mirror and connection ops. Finance keeps the business meaning. (Supersedes the earlier "lands in Finance" call, made before #1239/#1240 finished the G5 extractions.)
6. No `I<Section>ServiceRead` interfaces — the post-#1240 convention: the Contracts leaf is the public surface and carries only what other sections/Base actually consume; everything else stays `internal`.
7. **Tags are dead on the write side** (Peter, 2026-08-10 evening). They were a v1 workaround from before double-entry was understood. v2 books `items[].account` (the mapped 629 expense-account id) at doc creation, so the doc is right from the start; actuals-vs-budget derives from the ledger per 629 account joined through the category map — no matching guesswork. v2 has no tag-update endpoint anyway (confirmed in the OpenAPI spec), so retag-on-recategorize is deleted; a mis-booked doc is reclassified inside Holded and the mirror pulls the correction back. Read-side tag matching survives only for the legacy-doc unmatched queue.

## Section architecture

**`Humans.Holded` (new vertical section, G5-shaped like Finance):**
- Owns tables (own `HoldedDbContext`, sentinel `holded_ledger_lines`, history `__EFMigrationsHistory_Holded`): `holded_ledger_lines` (moves from Finance), `holded_accounts`, `holded_api_calls`, `holded_sync_states` (kind-keyed: Ledger / Accounts / FullSync).
- Owns the ledger/accounts sync + balance reconciliation, and the `/Holded` admin screen (its overview view models stay internal).
- `Humans.Holded.Contracts` (references `Humans.Interfaces` only, like Finance.Contracts): `IHoldedService` — ledger-line reads by account, all-lines read for grouping, account names/balances, and the sync trigger (consumed by the nightly job in Base and by Finance).

**`Humans.Finance` keeps the business meaning:** `holded_category_map`, `holded_expense_docs` (+ matching), `holded_creditor_contacts`, provisioning, actuals, creditor screens. Its ledger-line reads switch from its own repository to `IHoldedService`. Its purchase-doc sync keeps state in a new Finance-owned `holded_doc_sync_state` singleton row (the old shared `holded_sync_states` singleton moves to Holded re-keyed). The `/Finance/Creditors` **Resync button (#1241) relocates to `/Holded`**.

**Connector stays Base:** `IHoldedClient`/`HoldedClient` (Application/Infrastructure) — consumed by Expenses, Finance, and Holded. `HoldedSyncJob` stays in `Humans.Infrastructure/Jobs` (no discovery seam for recurring jobs) and calls `IHoldedFinanceService.SyncAsync` + `IHoldedService.SyncLedgerAsync(full: false)`.

**Table moves cost nothing:** every table the Holded section takes over is a re-derivable mirror — the Finance migration drops them, the Holded migration creates them, the first sync refills them. No data migration.

## 1. v2 client

Rewrite `HoldedClient` (`src/Humans.Infrastructure/Services/Holded/`) against `api/v2`:

- **Auth:** `Authorization: Bearer <token>` (replaces the v1 `key` header). Config stays `HOLDED_API_KEY` env var; same skip-when-unset behavior for PR-preview/local envs.
- **Pagination:** one private cursor-pager helper (`limit=200`, follow `cursor` while `has_more`). Existing page-count safety caps stay (log when a cap truncates — no silent caps).
- **429:** honor `Retry-After` (bounded wait + single retry), then surface as `HoldedTransientException`. Keep the transient/permanent exception split.
- **Dates:** parse `DD/MM/YYYY` explicitly (`es` format — do not let invariant-culture parsing swap day/month).
- **Endpoint map** (v1 → v2):
  | v1 | v2 |
  |---|---|
  | `POST/PUT /invoicing/v1/documents/purchase[/{id}]` | `POST/PUT /v2/purchases[/{id}]` |
  | `POST .../purchase/{id}/attach` | `POST /v2/purchases/{id}/attachments` |
  | `GET /invoicing/v1/documents/purchase` | `GET /v2/purchases` |
  | `GET/POST /invoicing/v1/expensesaccounts` | `GET/POST /v2/expenses-accounts` |
  | `GET/POST/PUT /invoicing/v1/contacts[/{id}]` | `GET/POST/PUT /v2/contacts[/{id}]` |
  | `GET /accounting/v1/dailyledger` | `GET /v2/ledger-entries` |
  | — | `GET /v2/accounting-accounts` (new: chart of accounts + totals) |
  | — | `GET /v2/usage` (new: Holded's own meter) |
- `IHoldedClient` + DTOs (`src/Humans.Application/Interfaces/Holded/`) updated in place to v2 shapes. No renames for their own sake; method-level parity with today's surface plus `ListAccountingAccountsAsync` and `GetUsageAsync`.

### Call metering

Every client call appends a record to an in-memory `ConcurrentQueue` on a singleton (`IHoldedCallLog`, registered with the connector): timestamp, endpoint template, method, status code, last-seen `X-RateLimit-Remaining`/`-Window`. `HoldedFinanceService` drains the queue to the repository (new `holded_api_calls` table) during syncs and on admin-screen load. Rationale: a `DelegatingHandler` writing to the DB would bypass the repository/service layering (hard rules); the queue keeps the client DB-free. Crash-loss of a few buffered rows is acceptable — `GET /usage` is the authoritative counter; ours adds per-endpoint/per-day granularity and history beyond the current period.

## 2. Data model

**Holded section (`HoldedDbContext`):**

| Table | Change |
|---|---|
| `holded_ledger_lines` | **Moves from Finance; widens to full mirror** — no account filter in the sync; all accounts, full history. Schema unchanged (unique `(EntryNumber, Line)`, index on `AccountNum`). |
| `holded_accounts` | **New** — chart-of-accounts cache: `Number` (key), `HoldedId`, `Name`, `Group`, `Debit`, `Credit`, `Balance` (decimals), `Archived`, `SyncedAt`. Refreshed each sync (full replace upsert). Feeds admin account list, reconciliation, and account names. |
| `holded_api_calls` | **New** — per-call metering: `Id`, `CalledAt`, `Method`, `Endpoint` (template, not full URL), `StatusCode`, `RateLimitRemaining?`, `RateLimitWindow?`. Monthly stat = `GROUP BY` calendar month. No pruning (rows are tiny; revisit if it ever matters). |
| `holded_sync_states` | **Moves from Finance, re-keyed** from singleton `Id=1` to one row per sync kind (`Ledger`, `Accounts`, `FullSync`): `Kind` (key), `SyncStatus`, `LastSyncAt`, `LastError`, `LastCount`. Lazy-seeded on first use. |

**Finance section (`FinanceDbContext`):** keeps `holded_category_map`, `holded_expense_docs` (with `ApprovedAt` → `IsApproved`, see plan), `holded_creditor_contacts`; gains `holded_doc_sync_state` (singleton, lazy-seeded) for the purchase-doc sync status.

Schema migrations only — no data migrations. Moved tables are dropped by the Finance migration and created by the Holded migration; the first sync refills them.

## 3. Sync algorithm

All ledger reads stay derived from `holded_ledger_lines` (June design unchanged): balance = Σdebit − Σcredit, owed = max(0, Σcredit − Σdebit), page loads cost 0 Holded calls.

- **Backfill (cold cache, and on Full sync):** `ledger-entries` from company inception (constant, e.g. 2020-01-01) → today, no account filter, cursor-paged. Falls back to year-chunking only if the API rejects the range. Full-replace semantics: upsert every fetched row on `(EntryNumber, Line)`, delete local rows in the range not present in the fetch.
- **Nightly incremental:** one trailing **45-day** window ending **now** — #1241's anchoring rationale carries over (accounting-date filter ⇒ never anchor on the newest cached line), but the window shrinks from 364 days because the balance reconciliation below catches anything older at one call/night, and the plan-tier quota makes a year-wide sweep (~20 pages/night) too expensive. Upsert on `(EntryNumber, Line)`; delete local rows inside the window that the fetch no longer contains; an empty fetch still deletes (append-only lingering is the stale-line bug — phantom €23 debit on 40000004). Truncated pagination hard-fails before any deletion. #1241's non-blocking sweep gate (in-process `SemaphoreSlim`, skip-and-report, single-server) carries over.
- **Reconcile (every sync, ~1 call):** `GET /accounting-accounts` → upsert `holded_accounts`, then compare each account's Holded balance against the local ledger sum. Mismatch → targeted re-pull for that account (`ledger-entries?account=N`, full history, replace semantics) → still mismatched → `LastError` on the Ledger sync state, surfaced on the admin screen. This catches backdating and edits older than the trailing window automatically — it supersedes #1241's manual-resync-only answer to backdated entries, though the Full sync button remains. Verified live: account 40000004 local sum −53,203.00 == chart balance.
- **Purchase docs:** current `SyncAsync` behavior ported to `GET /v2/purchases` (still full repage under safety cap; if v2 supports date filters, implementer may use them — not load-bearing).
- **Outbox push (Expenses → Holded):** unchanged flow, v2 endpoints (`purchases` create/update + attachments, `contacts`).

## 4. Admin screen — `/Holded`

`HoldedController` in the Holded section project, `[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]` (same audience as `/Finance`). The purchase-doc sync status and totals it shows for Finance-owned data come via `IHoldedFinanceService` (ordinary cross-section service read from the controller). View sections:

1. **Connection card:** Holded meter from `GET /usage` (period, usage, limit, by-type breakdown) beside our local count for the month; last-seen rate-limit remaining/window; API key present yes/no.
2. **Calls per calendar month:** table from `holded_api_calls` (month, total, by endpoint).
3. **Sync status:** one row per sync kind — status, last run, last count, last error.
4. **Buttons:** **Sync now** (incremental + reconcile), **Full sync** (inception backfill + reconcile), **Refresh accounts** (chart-of-accounts only). Each enqueues the Hangfire job (existing `RunHoldedSync` pattern, `[DisableConcurrentExecution]`).
5. **Account list:** from `holded_accounts` joined with local sums — number, name, group, Holded balance, local balance, local row count, reconciliation ✓/✗; each row links to the GL page.
5b. **GL account page `/Holded/Accounts/{number}`** — the per-account general-ledger view for ANY account (departments, banks, Stripe, creditors): header from the accounts cache, all cached journal lines below, native Holded sign. The Finance creditor statement (`/Finance/Creditors/{num}`) stays as the member-facing wrapper: **inverted balance** (+ = owed to the person) and a contact header (name/trade name/email/phone/IBAN/tax code/address from the cached v2 contact); the Creditors list likewise shows the inverted balance, drops the Owed column, and sorts on every column.
6. **Department actuals (629\*):** per-account booked totals for the `629*` block straight from `holded_accounts` — number, name, debit/balance, share of total. Truer than doc-matching-based `HoldedActualsByCategory` (verified live 2026-08-10: 467,295.09 total, of which 133,416.45 — 28.5% — sits in the `62900000` catch-all that doc-matching can't see).
7. **Totals:** ledger lines cached, purchase docs, bound creditor contacts, outbox pending.

`GET /usage` is called on screen load (1 call) — acceptable; everything else renders from local tables.

## 5. Jobs

- `HoldedSyncJob` (nightly, stays in `Humans.Infrastructure/Jobs`): `IHoldedFinanceService.SyncAsync` (purchase docs) then `IHoldedService.SyncLedgerAsync(full: false)` (trailing window → accounts refresh → reconcile + targeted re-pulls → drain call-log queue).
- Full sync: `SyncLedgerAsync(full: true)` from the `/Holded` button (serialized by the sweep gate).
- `HoldedExpenseOutboxJob`: unchanged cadence, v2 endpoints underneath.

> This spec file moves to `src/Sections/Humans.Holded/Docs/` when the section project is created (sections carry their own docs).

## 6. Testing

- Client: canned v2 JSON fixtures (shapes above, incl. `DD/MM/YYYY` dates and string decimals); 429/`Retry-After` path; cursor paging; metering queue records.
- Sync: EF-InMemory (per project rule) — backfill replace semantics, incremental overlap upsert, window delete-detection, reconciliation mismatch → targeted re-pull.
- Admin screen: service-level tests for the view model assembly (monthly grouping, account join).

## Follow-up PR (specced here, not today's build): bank reconciliation panel

A read-only worklist on `/Finance/Holded` — Peter executes all writes in the Holded GUI (decision 2026-08-10: no API writes for now).

- **New `holded_bank_movements` table** mirroring `GET /treasury/accounts` + `GET /treasury/accounts/{id}/bank-movements` (id, account, description, amount, booking date, `status` pending/partial/reconciled, origin), refreshed by the nightly sync (~2 calls).
- **Panel per bank account:** unreconciled movement count + list; bank-feed balance vs local ledger-sum gap.
- **Duplicate-entry detector:** same-date-same-amount ledger-line pairs on `572*` accounts — catches the transfer-doubling failure where reconciling each side of an inter-account transfer creates its own journal entry (5 live Stripe-payout doubles found 2026-08-10, e.g. #1854 "STRIPE PAYOUT" + #1937 "Abono transferencia de stripe", both D 1,358.25 on 08/06).
- **Match suggestions:** unreconciled movements paired against pending purchases by exact amount + normalized counterparty-name overlap (dry-run 2026-08-10: 5 confident matches out of 53 unreconciled; the rest lacked a purchase doc or were inter-account transfers).
- Reconcile-via-API (`POST .../bank-movements/{movementId}/reconcile`, body `documents: [{document_id, document_type}]`) exists and supports split reconciliation — wire buttons only if Peter later grants a write key.

## Future (recorded, unscheduled): Pleo connector

~5 members hold Pleo cards funded by a 30k transfer from Sabadell. Ledger `57200003` shows D 30,000.00 / C 71.97 — **~29,928 of card spend is booked nowhere in Holded** and invisible to the 629\* actuals. Ideal end state: pull Pleo's transaction API, book each transaction into Holded against the right `629*` account (requires a Holded write key + a category mapping), giving one consolidated spend view. Separate design when picked up.

## Out of scope

- Webhooks (deferred — revisit if same-day purchase/payment freshness is ever wanted).
- The `/Expenses` top-card fix (in flight in another session; the full mirror gives it complete data for account 40000004, but the card's own filter bug is not this spec's).
- Any expense-report lifecycle changes (June design already settled those).
- Recategorizing the `62900000` catch-all (manual, with the production coordinator, in the Holded GUI).

## Implementer notes

- Read-only dev token at `C:\Users\PeterDrier\.holded\dev-token` (Peter's machine) for probing real shapes; it shares the account — keep exploratory calls minimal. It cannot write; write paths are testable only via fixtures until QA.
- **Full OpenAPI spec: `https://api.holded.com/openapi/api2.json`** (~4.5 MB, 205 paths) — use it for exact request/response schemas instead of the doc pages.
- `GET /usage/{type}` exists for per-type detail; only needed if `secondary_usages` on the main call proves insufficient.
- Account `40000004` full history is 6 rows / −53,203.00 balance — a handy known-good reconciliation target.
- **Chart totals can exclude ledger lines:** live 2026-08-10, `accounting-accounts` showed 57200001 debit 418,840.54 while summed `ledger-entries` gave 771,074.85 — off by exactly one entry (#2412, 352,234.31, likely draft/unconfirmed). The reconciliation check must therefore *report* a mismatch with the delta and nearest-amount candidate entries — never hard-fail or loop re-pulls; a standing known mismatch is displayable state on the admin screen.
