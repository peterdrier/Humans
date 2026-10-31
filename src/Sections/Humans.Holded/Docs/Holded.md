<!-- freshness:triggers
  src/Sections/Humans.Holded/**
  src/Sections/Humans.Holded/Contracts/**
  src/Sections/Humans.Expenses/Jobs/HoldedExpenseOutboxJob.cs
-->

# Holded (section) — Section Invariants

## Purpose

Holded (the company) is a SaaS vendor that holds the association's accounting books and records.
This section is our interface and conduit to that vendor. It does two jobs and keeps them apart:
it keeps a cached copy of the book so no page ever waits on Holded, and it is the one connector
other sections use to book, pay and invoice. Holded's API has quotas, so keeping that cache
current and metering every call against the allowance is part of the job.

Good looks like: every balance a member or admin sees comes from the cache, the cache is never
silently short, and every call is counted. The section's own pages show the cached copies and the
API-call accounting, nothing more. Out of scope: summaries and decisions about money (Finance owns
them; Expenses and Store decide in their own lanes), keying anything by member, and mirroring the
bank feed.

The **ledger mirror**: a local, re-derivable copy of Holded's daybook and chart of accounts,
plus the sync that maintains it and the `/Holded` admin screen. Every cross-section ledger
read is served from this cache — zero Holded calls per page view. The HTTP connector belongs
to this section too and has its own doc ([`Holded-connector.md`](Holded-connector.md)).

## Concepts

- **Mirror, not source**: every table is reconstructible from the Holded API. Dropping and
  resyncing is always safe; no data migrations ever.
- **Replace semantics**: a sweep is the truth for its window — cached lines the sweep no longer
  returns are deleted (an empty fetch still deletes). Never append-only: a line Holded deleted
  or reclassified would linger forever.
- **The connector fails closed.** Every read and write rule of `IHoldedClient` (required
  identities (including expense-account, contact and sales-document IDs) and amounts (including the pending balance on approved purchases), complete pagination, typed write responses, the `unconfirmed:`
  references, the 429 policy) lives in [`Holded-connector.md`](Holded-connector.md). The mirror
  depends on one consequence: no short or fabricated page ever reaches replace semantics.
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

Every page under `/Holded` reads the mirror and links to the others (Summary, Accounts, API calls,
and one "Finance" link to `/Finance/Holded`). Each page does one thing.

- `/Holded` — summary: configured or not (`IsConfigured`), live API usage as one line (GET
  /usage, skipped when not configured; budget = `Holded:MonthlyCallBudget`, default 2000), per
  sync kind the last run, status, count and error, mirror counts (lines, entries, non-archived
  accounts), the number of accounts where mirror ≠ Holded (linked to `/Holded/Accounts?mismatched=true`),
  and the Sync now / Full resync buttons with the full resync's cost estimate (one call per 200
  lines plus the chart; 42 today).
- `/Holded/Accounts` — the chart grouped by PGC group (English group name from the leading digit,
  not Holded's Spanish `group` string). Groups are collapsed, showing account count, Holded and
  mirror balance, lines and mismatches; expanded rows show number, Holded name, English name,
  both balances, lines and the ✓/✗. Archived and empty accounts (no cached line, no Holded totals,
  no disagreement) are hidden by default, each with a toggle (`?archived=true`, `?empty=true`);
  `?mismatched=true` shows only non-archived accounts that disagree. An account seen only on a
  line (not yet in the chart) is listed, with a zero Holded balance.
- `/Holded/Accounts/{number}` — the general-ledger page for ANY account (departments, banks,
  creditors): header balances, and every cached line with its counterparty, signed Amount, Holded's
  entry id and its manual Checked tick. Counterparties are read from the line's own entries only.
  "See in Holded" opens the account's own ledger at `app.holded.com/accounting/ledger/{number}`.
- `/Holded/Entries/{number}` — every leg of one journal entry (account + name, type, description,
  signed Amount, Checked) and the entry's Holded id, no balancing total row. 404 when no cached
  line carries the entry number. No link into Holded's app yet: its entry URL is unverified.
- `/Holded/Calls` — API calls by Madrid month and endpoint, from `holded_api_calls`.

**English names** come from `PgcAccountNames`, a static dictionary of the PGC PYMES chart (plus
the non-profit adaptation's own accounts) keyed by number prefix, longest matching prefix first.
It is applied at render time only, and only when Holded's name is the standard Spanish one for
that prefix (case, accents and spacing ignored; a letter-free suffix such as "21%" is kept).
Member accounts (40xxxxxx, 41xxxxxx) and the 629 category accounts always keep their own name.
The mirror stores only what Holded returned.

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
ownership: these screens cover what `HoldedDbContext` owns, that one covers what `FinanceDbContext`
does. They link to each other rather than restating each other's figures; nothing of Finance's is
shown here.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Finance admin / Admin | Everything above (`FinancePolicies.Manage`). |
| Board | Read the summary, accounts, statements, entries and calls; trigger no sync (`FinancePolicies.Read`). |
| Other sections | Read via `IHoldedService`; trigger sync (nightly job). |
| Any human | None directly. |

## Invariants

- The Accounts controller orders PGC groups by leading digit and each group's accounts by number. Service grouping, filters and totals do not impose display order; summary counts are independent of row order.

- The summary controller orders sync-state rows by kind using ordinal comparison, and the calls controller orders Madrid month buckets newest first. Services supply the state and aggregated counts without display ordering.

- `GetChartAsync` returns mirror account metadata, including archived accounts, without display ordering. Workgroups' account pickers order by number in their views.

- Vendor-error IBAN redaction recognizes valid human-formatted values even when whitespace or hyphens split the country code and check digits.

- Operator timestamps and ledger dates use the shared display formatters in Europe/Madrid time; monthly usage labels use the shared month/year formatter. Date ordering and month names follow the UI culture, and missing sync stamps still read "never".

- Nightly sweep = one trailing **45-day** window anchored on *now* (accounting-date filter ⇒
  never anchor on the newest cached line); full sweep (inception → today) on cold cache or the
  Full sync button. Reconciliation catches anything older, so correctness does not depend on
  window width — the window is a quota choice.
- Sweeps are serialized by a non-blocking in-process gate; a second caller is skipped and told
  so, never queued (single-server deployment).
- Reads (`IHoldedService`: lines, balances, entry, purchase lines, open document amounts, sums by account,
  chart) never call Holded, and nothing loads the whole ledger table: balances and sums are grouped
  in the database, statements load only their own entries. Amounts are Holded's raw sign.
- `GetOpenDocumentAmountsAsync` is Σ (credit − debit) over `purchase`, `purchaserefund` and `payment`
  lines per account: what documents leave open, not the balance (hand-booked entries and collections
  are left out).
- A ledger line keeps Holded's `id` as `EntryId` (the journal entry's id, shared by every line of
  the entry, so never a key: the natural key stays `(EntryNumber, Line)`) and `checked` as
  `Checked` (Holded's manual tick, not bank reconciliation; null until the line is re-synced).
  `doc_description` and `tags` are not stored.
- API-call metering drains honour cancellation before consuming the buffer, then persist consumed records independently of request cancellation. Overview reads remain cancellable afterward.
- The treasury bank feed (`IHoldedClient.ListBankMovementsAsync` /
  `ReconcileBankMovementAsync`) is **not** mirrored here — Finance's SEPA booking flow
  (nobodies-collective/Humans#1185) reads it live over a bounded window; its rules are in
  [`Holded-connector.md`](Holded-connector.md). A follow-up reconciliation panel is where a
  bank-movements mirror table would land; see `2026-08-10-holded-v2-migration-design.md`.
- Nothing here is keyed by member, so this section holds no consent gate and no erasure path.
  It is not free of personal data: a creditor account's name is the member's and the lines on it
  are their reimbursement history, identifiable through Finance's member→creditor binding.
  Finance's GDPR contributor exports those lines (its `CreditorLedger` slice, read through
  `IHoldedService.GetLedgerLinesAsync`) and declares their fiscal-retention basis.

## Negative Access Rules

- Only this section's repository touches `HoldedDbContext`.
- This section never reads Finance's tables and calls no Finance service. Its only Finance
  reference is `FinancePolicies`, the policy names its pages are gated by.

## Triggers

- `HoldedSyncJob` (nightly at 03:00, this section's `Jobs/`; scheduled by this section's `ISectionJobs`, job id `holded-sync`): `SyncLedgerAsync(full: false)` only. Finance's purchase-doc sync is Finance's own job (`HoldedDocSyncJob`, 02:45); the sweep reads none of its output. Both jobs take the shared `holded-api` Hangfire job lock, so they never run on the rate-limited API at once.
- `/Holded` buttons: `SyncNow` (incremental + reconcile, ledger only), `FullSync`.

## Cross-Section Dependencies

- Outbound: `IHoldedClient` (this section's own connector). Finance's `FinancePolicies` constants only.
- Inbound: Finance (creditor statuses/statements/actuals via `IHoldedService`), the nightly job.

## Architecture

**Owning section:** `Holded` (`src/Sections/Humans.Holded`)
**Public contract:** `Humans.Holded.Contracts` (the `Contracts/` folder): `IHoldedService` (+ `HoldedLedgerLineInfo`, `HoldedChartAccountInfo`) for the mirror, and the connector surface — `IHoldedClient` and its DTOs (see [`Holded-connector.md`](Holded-connector.md))
Connector DTOs expose consumed fields only; usage reads contain the API period and call count, and contact creation carries no unsupported custom user id.

Purchase-document detail reads expose only approval time, notes and payment details. Document totals, identifiers and tags come from the purchase list when needed; the detail parser ignores those unused fields.

**Connector implementation:** `HoldedClient` delegates to internal purchases, sales, contacts, accounting and treasury clients. One shared `HoldedTransport` owns auth, retry, paging, metering and timing; endpoint payloads and parsers stay with their operation group. The existing public client contract and typed-client registration are unchanged.
**Owned tables:** the four above
**Target shape:** [`health.md`](health.md); the single-mirror design is
the 2026-10-30 Holded section-flow record (phase 1 built the reads and
pages; the callers still on `IHoldedClient` are ledger debt `CENTRAL-105`). Historical design record:
[`2026-08-10-holded-v2-migration-design.md`](2026-08-10-holded-v2-migration-design.md).
