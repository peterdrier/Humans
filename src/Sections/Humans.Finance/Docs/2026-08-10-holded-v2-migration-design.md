# Holded API v2 migration + full ledger mirror + Holded admin screen — design

**Status:** Draft — awaiting Peter's sign-off
**Date:** 2026-08-10
**Supersedes:** the sync mechanics of `2026-06-15-holded-ledger-single-source-design.md` (its "daybook is the single source, cache it, derive everything" decision stands; the v1 `dailyledger` transport and creditor-range scoping do not).

## Context

Holded launched API v2 (June 2026): REST/JSON at `https://api.holded.com/api/v2`, Bearer auth, cursor pagination. v1 is deprecated. Facts confirmed against the live account with a read-only token (not doc excerpts):

- `GET /usage` → `{"type":"automation_token","period":"2026-08","usage":36,"limit":2000000,"count":1,"secondary_usages":{"api_v1_legacy_1":35,...}}`. **Real monthly limit is 2,000,000** — quota is a non-issue at our volume, but calls are metered per type and the endpoint is free to read.
- `GET /accounting-accounts?include_empty=false` → 267 active accounts; each item: `id, color, number, name, description, group, debit, credit, balance` (decimals as strings), `archived, non_deductible`. Optional `start_date`/`end_date` scope the totals; omitted = all-time. **Per-account all-time debit/credit/balance in one call** — this powers reconciliation.
- `GET /ledger-entries?start_date=&end_date=[&account=][&limit=≤200][&cursor=]` → `{items[], cursor, has_more}`; item: `entry_number, line, date, type, description, doc_description, account, debit, credit` (strings), `tags[], checked`. **Dates are `DD/MM/YYYY`**, not ISO. No `updated_since` filter. A 2.5-year range worked in one call (no v1-style 1-year window cap observed); chunk by year only if the API starts rejecting ranges.
- v2 equivalents exist for every v1 call we make: `purchases` CRUD + `/attachments` (+ `/approve`, `/payments`), `contacts`, `expenses-accounts`, plus `webhooks` management.
- Webhooks (HMAC-SHA256-signed, retried with backoff) cover purchases/payments/contacts — **no events for raw ledger entries**, so they can't replace ledger polling. **Deferred** (decision below).
- 429 handling: `Retry-After` header must be honored; `X-RateLimit-*` headers identify the tripped window (`minute` or `month`).

## Decisions (Peter, 2026-08-10)

1. v2 migration now; **v1 code deleted in the same PR** — no dual-path period.
2. Webhooks: later. Nightly polling is 2–3 calls/night.
3. Ledger mirror widens to **all accounts, full history** (was: creditor range `40000000–40000099` only).
4. Admin screen gets a **Full sync** button alongside incremental sync.
5. Finance project extraction already happened (#1239) — v2 work lands in `src/Sections/Humans.Finance` / shared connector files, no move needed.

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

## 2. Data model (all Finance-owned, `FinanceDbContext`)

| Table | Change |
|---|---|
| `holded_ledger_lines` | **Widen to full mirror** — drop the `CreditorAccountMin/Max` filter in the sync; all accounts, full history. Schema unchanged (unique `(EntryNumber, Line)`, index on `AccountNum`). |
| `holded_accounts` | **New** — chart-of-accounts cache: `Number` (key), `HoldedId`, `Name`, `Group`, `Debit`, `Credit`, `Balance` (decimals), `Archived`, `SyncedAt`. Refreshed each sync (full replace upsert). Feeds admin account list, reconciliation, and account names on Creditors screens. |
| `holded_api_calls` | **New** — per-call metering: `Id`, `CalledAt`, `Method`, `Endpoint` (template, not full URL), `StatusCode`, `RateLimitRemaining?`, `RateLimitWindow?`. Monthly stat = `GROUP BY` calendar month. No pruning (rows are tiny; revisit if it ever matters). |
| `holded_sync_states` | **Re-key from singleton `Id=1` to one row per sync kind** (`Ledger`, `Accounts`, `PurchaseDocs`, `FullSync`): `SyncKind` (key), `SyncStatus`, `LastSyncAt`, `LastError`, `LastCount`. Lazy-seeded on first use; no data backfill (statuses are ephemeral). |

Schema migrations only — no data migrations. The widened mirror fills itself via the backfill job.

## 3. Sync algorithm

All ledger reads stay derived from `holded_ledger_lines` (June design unchanged): balance = Σdebit − Σcredit, owed = max(0, Σcredit − Σdebit), page loads cost 0 Holded calls.

- **Backfill (once, and on Full sync):** `ledger-entries` from company inception (constant, e.g. 2020-01-01) → today, no account filter, cursor-paged. Falls back to year-chunking only if the API rejects the range. Full-replace semantics: upsert every fetched row on `(EntryNumber, Line)`, delete local rows in the range not present in the fetch.
- **Nightly incremental:** `start_date = max(local line date) − 7 days`, `end_date = today`. Upsert on `(EntryNumber, Line)`; delete local rows inside the window that the fetch no longer contains.
- **Reconcile (every sync, ~1 call):** `GET /accounting-accounts` → upsert `holded_accounts`, then compare each account's Holded balance against the local ledger sum. Mismatch → targeted re-pull for that account (`ledger-entries?account=N`, full history, replace semantics) → still mismatched → `LastError` on the Ledger sync state, surfaced on the admin screen. This catches retroactive edits/voids outside the 7-day window; verified live: account 40000004 local sum −53,203.00 == chart balance.
- **Purchase docs:** current `SyncAsync` behavior ported to `GET /v2/purchases` (still full repage under safety cap; if v2 supports date filters, implementer may use them — not load-bearing).
- **Outbox push (Expenses → Holded):** unchanged flow, v2 endpoints (`purchases` create/update + attachments, `contacts`).

## 4. Admin screen — `/Finance/Holded`

Actions on `FinanceController` (Finance section project), same authorization as the other `/Finance` screens. View sections:

1. **Connection card:** Holded meter from `GET /usage` (period, usage, limit, by-type breakdown) beside our local count for the month; last-seen rate-limit remaining/window; API key present yes/no.
2. **Calls per calendar month:** table from `holded_api_calls` (month, total, by endpoint).
3. **Sync status:** one row per sync kind — status, last run, last count, last error.
4. **Buttons:** **Sync now** (incremental + reconcile), **Full sync** (inception backfill + reconcile), **Refresh accounts** (chart-of-accounts only). Each enqueues the Hangfire job (existing `RunHoldedSync` pattern, `[DisableConcurrentExecution]`).
5. **Account list:** from `holded_accounts` joined with local sums — number, name, group, Holded balance, local balance, local row count, reconciliation ✓/✗. Filter/sort client-side (267 rows).
6. **Totals:** ledger lines cached, purchase docs, bound creditor contacts, outbox pending.

`GET /usage` is called on screen load (1 call) — acceptable; everything else renders from local tables.

## 5. Jobs

- `HoldedSyncJob` (nightly): incremental ledger → accounts refresh → reconcile (+ targeted re-pulls) → purchase-doc sync → drain call-log queue.
- Full sync: same job entry with a `full` flag, enqueued by the button.
- `HoldedExpenseOutboxJob`: unchanged cadence, v2 endpoints underneath.

## 6. Testing

- Client: canned v2 JSON fixtures (shapes above, incl. `DD/MM/YYYY` dates and string decimals); 429/`Retry-After` path; cursor paging; metering queue records.
- Sync: EF-InMemory (per project rule) — backfill replace semantics, incremental overlap upsert, window delete-detection, reconciliation mismatch → targeted re-pull.
- Admin screen: service-level tests for the view model assembly (monthly grouping, account join).

## Out of scope

- Webhooks (deferred — revisit if same-day purchase/payment freshness is ever wanted).
- The `/Expenses` top-card fix (in flight in another session; the full mirror gives it complete data for account 40000004, but the card's own filter bug is not this spec's).
- Any expense-report lifecycle changes (June design already settled those).

## Implementer notes

- Read-only dev token at `C:\Users\PeterDrier\.holded\dev-token` (Peter's machine) for probing real shapes; it shares the account — keep exploratory calls minimal. It cannot write; write paths are testable only via fixtures until QA.
- `GET /usage/{type}` exists for per-type detail; only needed if `secondary_usages` on the main call proves insufficient.
- Account `40000004` full history is 6 rows / −53,203.00 balance — a handy known-good reconciliation target.
