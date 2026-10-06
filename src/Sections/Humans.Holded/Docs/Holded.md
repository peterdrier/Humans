<!-- freshness:triggers
  src/Sections/Humans.Holded/**
  src/Sections/Humans.Holded/Contracts/**
  src/Sections/Humans.Expenses/Jobs/HoldedExpenseOutboxJob.cs
-->

# Holded (section) — Section Invariants

The **ledger mirror**: a local, re-derivable copy of Holded's daybook and chart of accounts,
plus the sync that maintains it and the `/Holded` admin screen. Every cross-section ledger
read is served from this cache — zero Holded calls per page view. The HTTP connector belongs
to this section too and has its own doc ([`Holded-connector.md`](Holded-connector.md)).

## Concepts

- **Mirror, not source**: every table is reconstructible from the Holded API. Dropping and
  resyncing is always safe; no data migrations ever.
- **Replace semantics**: a sweep is the truth for its window — cached lines the sweep no longer
  returns are deleted (an empty fetch still deletes). Append-only caching was the phantom-row
  bug (deleted/reclassified lines lingering forever).
- **Account identities are required**: expense-account numbers must be present so provisioning can avoid remote collisions; chart account IDs must be nonblank so invoicing never sends an empty account reference. Incomplete pages fail at the connector.
- **Integer identities stay exact**: fractional chart/account numbers and ledger entry/line numbers fail the connector read rather than being truncated onto a different cached identity.
- **Rate-limit retries are read-only**: the connector retries a content-free GET once after 429; writes, including bodyless sales-approval POSTs, surface the transient failure without an automatic retry.
- **Page rows are required**: null items fail the complete connector read as a permanent error; silently dropping malformed rows must never turn a page into an incomplete or empty reconciliation input.
- **Cursor walks must progress**: a repeated cursor fails the complete read immediately, before refetching that page. Distinct cursors retain each endpoint's page cap; incomplete lists never reach replace-semantics reconciliation.
- **Creation identities are required**: accepted purchase/contact/sales/expense-account creates must return a nonblank ID. Null bodies or missing/blank IDs fail permanently, so callers cannot automatically repeat an already accepted create or persist an unusable account reference. A contact update may reuse its known ID when the parsed response omits or blanks it.
- **Write responses stay typed**: unreadable purchase-create, contact-upsert, sales-create and expense-account-create success bodies fail as permanent connector errors, including text-decoding failures, so callers can record failure rather than losing queue bookkeeping to a raw parsing exception. Accepted payments and ledger postings retain their operation-specific `unconfirmed:` reference when the success body cannot be decoded or parsed; callers must not repeat a posting because its response was unreadable.
- **Purchase document identity is required**: purchase documents need a nonblank ID; a missing or blank ID rejects the page. An absent `payments_pending` reads as 0 (nothing owed), which refuses a booking rather than over-paying.
- Sales-document totals are required: a missing or null `total` fails as a permanent connector error before Store can finish issuance or compare a recovered invoice. Explicit zero remains valid.
- **Chart totals are required**: missing or null debit, credit or balance fails the account page as a permanent connector error before replacing cached totals. Explicit zero totals remain valid.
- **Reconciliation**: after every sweep, each non-archived account's chart balance is compared
  to the local ledger sum; drifted accounts get one targeted full-history re-pull (capped at 10
  per run, logged when capped). Residual mismatches are **reportable state** on the sync row,
  never a failure — Holded's own chart totals can exclude unconfirmed entries.

- Saved ledger error text stays within 2000 UTF-16 code units without splitting a Unicode surrogate pair. The original exception still propagates in full, and the sync gate is released.

- **Interrupted manual syncs** remain recorded as `Error` and propagate caller cancellation. Caller-aborted document/ledger syncs and their controller log stack-free warnings; dependency failures remain errors with exceptions.

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
  Account balances read in the association's POV (below).
- `/Holded/Accounts/{number}` — the general-ledger page for ANY account (departments, banks,
  creditors): header balances, and every cached line with its counterparty and signed Amount,
  both in the association's POV. The Entry cell links to the entry page below; "See in Holded"
  opens the account's own ledger at `app.holded.com/accounting/ledger/{number}`.
- `/Holded/Entries/{number}` — every leg of one journal entry (account + name, type,
  description, signed Amount), no balancing total row. 404 when no cached line carries the
  entry number.

### Association-POV sign convention

No page under `/Holded` shows a Debit or Credit column — every ledger row and every account
balance (`HoldedBalance`, `LocalBalance` on `/Holded` and `/Holded/Accounts/{number}`) is a
single signed `Amount`: **+ means the association's money went up, − means it went down.**
Because assets and income carry opposite bookkeeping signs, "money up" maps to the account
group (the number's leading digit): groups 1–5 (equity, assets, banks, debtors, creditors) show
`Debit − Credit`; groups 6–9 (expenses, income, and their equity-charged counterparts) show
`Credit − Debit`. The flip is **display-only** — reconciliation (below) always compares the raw
`Debit − Credit` convention, before it is applied. `/Expenses` is a different page in a
different section and stays in the *user's* POV (+ means the user is owed money); it is
untouched by this convention.

### Counterparty

Each ledger row's counterparty is resolved from the entry's *other* legs posted to the opposite
raw side (a debit line's counterparties are the entry's credit lines) — Holded's API does not
return a contra account, so this is derived by grouping the mirror's lines on `EntryNumber`.
Exactly one opposing line shows that account, linked; more than one (e.g. a purchase invoice:
expense + VAT + creditor) shows the largest by absolute amount plus "+N more", linked to the
entry page instead.

**Not this section:** `/Finance/Holded` is Finance's own connector index — its purchase-doc sync
staleness, category map and pulled docs (nobodies-collective/Humans#1000). The split is the table
ownership: this screen covers what `HoldedDbContext` owns, that one covers what `FinanceDbContext`
does. The two link to each other rather than restating each other's figures; the doc-sync row shown
in the table above is still fetched through `IHoldedFinanceService.GetDocSyncInfoAsync`.

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
- API-call metering drains honour cancellation before consuming the buffer, then persist consumed records independently of request cancellation. Overview reads remain cancellable afterward.
- The treasury bank feed (`IHoldedClient.ListBankMovementsAsync` /
  `ReconcileBankMovementAsync`, `GET`/`POST /treasury/accounts/{id}/bank-movements[/…/reconcile]`)
  is **not** mirrored here — Finance's SEPA booking flow (nobodies-collective/Humans#1185) reads
  it live over a bounded window. A follow-up reconciliation panel is where a
  `holded_bank_movements` mirror table would land; see
  `2026-08-10-holded-v2-migration-design.md`.
- Nothing here is keyed by member, so this section holds no consent gate and no erasure path.
  It is not free of personal data: a creditor account's name is the member's and the lines on it
  are their reimbursement history, identifiable through Finance's member→creditor binding.
  Whether that owes an Article 15 slice is open — debt ledger, 2026-09-03.

## Negative Access Rules

- Only this section's repository touches `HoldedDbContext`.
- This section never reads Finance's tables; the admin screen obtains Finance's doc-sync row
  via `IHoldedFinanceService` in the controller.

## Triggers

- `HoldedSyncJob` (nightly at 03:00, this section's `Jobs/`; scheduled by this section's `SectionJobs`, job id `holded-sync`): Finance's doc sync, then `SyncLedgerAsync(full: false)`.
- `/Holded` buttons: `SyncNow` (incremental + reconcile), `FullSync`.

## Cross-Section Dependencies

- Outbound: `IHoldedClient` (this section's own connector) only.
- Inbound: Finance (creditor statuses/statements/actuals via `IHoldedService`), the nightly job.

## Architecture

**Owning section:** `Holded` (`src/Sections/Humans.Holded`)
**Public contract:** `Humans.Holded.Contracts.IHoldedService` (+ `HoldedLedgerLineInfo`)
**Owned tables:** the four above
**Target shape:** [`health.md`](health.md). Historical design record:
[`2026-08-10-holded-v2-migration-design.md`](2026-08-10-holded-v2-migration-design.md).
