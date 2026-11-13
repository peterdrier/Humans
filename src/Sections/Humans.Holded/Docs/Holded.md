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
them; Expenses and Store decide in their own lanes), and keying anything by member.

The **ledger mirror**: a local, re-derivable copy of Holded's daybook and chart of accounts,
plus the sync that maintains it and the `/Holded` admin screen. The **purchase-document mirror**
beside it holds every purchase document Holded lists, raw; which budget category one counts toward
is Finance's call, computed on read. Every cross-section ledger
read is served from this cache — zero Holded calls per page view. The HTTP connector belongs
to this section too and has its own doc ([`Holded-connector.md`](Holded-connector.md)).

## Concepts

- **Mirror, not source**: every table is reconstructible from the Holded API. Dropping and
  resyncing is always safe; no data migrations ever.
- **Replace semantics**: a sweep is the truth for its window — cached lines the sweep no longer
  returns are deleted (an empty fetch still deletes). Never append-only: a line Holded deleted
  or reclassified would linger forever.
- **The connector fails closed.** Its read and write rules live in
  [`Holded-connector.md`](Holded-connector.md). The mirror depends on one consequence: no short or
  fabricated page ever reaches replace semantics, so a failed pull leaves the last good mirror and
  an `Error` sync state, and a find-or-create write stops rather than reading an unreadable
  contact as absent.
- **Reconciliation**: after every sweep, each non-archived account's chart balance is compared
  to the local ledger sum; drifted accounts get one targeted full-history re-pull (capped at 10
  per run, logged when capped). Residual mismatches are **reportable state** on the sync row,
  never a failure — Holded's own chart totals can exclude unconfirmed entries.
- **A failed sync** of any kind marks its state `Error` with the message (cut to the column's
  2000 characters) and saves that even when the request was cancelled; the original exception
  still propagates and the gate is released. A sync its caller cancelled logs a warning without a
  stack; any other failure logs an error.

## Data Model (`HoldedDbContext`, history `__EFMigrationsHistory_Holded`)

| Table | Content |
|---|---|
| `holded_ledger_lines` | full daybook mirror, all accounts, unique `(EntryNumber, Line)` |
| `holded_accounts` | chart-of-accounts cache with Holded's debit/credit/balance totals |
| `holded_api_calls` | per-call metering (endpoint, status, rate-limit headers) |
| `holded_purchase_docs` | purchase-document mirror keyed on Holded's doc id: number, contact, description, Madrid date, amounts, approved (`draft == false`; absent reads as not approved), payments pending (null until re-synced), notes, doc + line tags, first line's booked account |
| `holded_bank_movements` | bank-feed mirror keyed on `(TreasuryAccountId, Id)`: date, signed amount, description, status. Window replace: a sync is the truth for its date window plus any id it returned |
| `holded_contacts` | contact mirror keyed on Holded's contact id: name, supplier account number, trade name, email, phone, mobile, IBAN, tax code, address. Full pull with replace semantics |
| `holded_sync_states` | one row per sync kind (`Ledger`, `Accounts`, `FullSync`, `PurchaseDocs`, `Contacts`, `BankMovements`), lazy-created |

Sentinel table is `holded_accounts` — created by this baseline alone, so its existence proves
the baseline ran. (`holded_ledger_lines` could not carry that proof: the historical chain and
pre-split Finance both created it.) Sections migrate in name order, so Finance's drop of its
mirror tables runs before this baseline recreates them.

## Routing

Every page under `/Holded` reads the mirror. Summary, Accounts, Purchases and API calls are the Holded admin nav
group's items, so the admin shell renders them as the tab strip; an account's or entry's page sits
under Accounts. Each page does one thing.

- `/Holded` — summary: configured or not (`IsConfigured`), live API usage as one line (GET
  /usage, skipped when not configured; budget = `Holded:MonthlyCallBudget`, default 2000), per
  sync kind the last run, status, count and error, mirror counts (lines, entries, non-archived
  accounts), the number of accounts where mirror ≠ Holded (linked to `/Holded/Accounts?mismatched=true`),
  and the Sync now / Full resync buttons with the full resync's cost estimate (one call per 200
  lines plus the chart).
- `/Holded/Accounts` — the chart grouped by PGC group (English group name from the leading digit,
  not Holded's Spanish `group` string), and within each group by PGC subgroup (first two digits,
  English name from `PgcAccountNames`; 40 suppliers, 41 creditors, 44 debtors, 47 public
  administrations). Groups and subgroups are collapsed, each showing account count, Holded and
  mirror balance, lines and mismatches; a group with one subgroup opens it. Expanded rows show number, Holded name, English name,
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
- `/Holded/Purchases` — the purchase-document mirror, newest first: date, number (opens the doc in
  Holded), contact and description, total, approved, booked account, tags, notes. Links to
  Finance's unmatched queue for attribution.
- `/Holded/Calls` — API calls from `holded_api_calls` as a pivot: one row per endpoint (busiest
  first), one column per Madrid month (oldest first), with row and column totals.

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

**Not this section:** budget attribution. Finance's account map (`/Finance/HoldedAccounts`) and
unmatched queue (`/Finance/HoldedUnmatched`) read the purchase mirror through `IHoldedService` and
match each doc against Finance's own category map; nothing of Finance's is shown here.

## Write face (`IHoldedWrite`)

Ensure-style writes for other sections, each `[ExternalWrite]`: the caller passes Humans keys
(account numbers, names, the text it wrote), the call finds the Holded ids it needs inside itself,
is safe to repeat, and returns an outcome, never a Holded id.

- `EnsureCreditorContactAsync` — contact by exact legal name among contacts with an account in
  40000000–41999999, whatever their type (hand-made contacts are `supplier`), else POST; returns
  `supplier_record.num`. Never PUTs a contact. No `custom_id` lookup until a live write confirms
  v2's create stores it.
- `EnsurePurchaseDocumentAsync` (no caller yet) — contact by account number (live contacts list); document by
  contact + description + total + notes (list, then GET for notes); else created with the line's
  `account` = the chart mirror's Holded id for the expense account number (the same id
  `expenses-accounts` uses). Notes are the document's Humans key and must name one expense line, or
  identical lines collapse into one document. Attaches on create, or when the caller says its earlier
  attachment did not go up: Holded's v2 purchase GET has no attachment field, so the caller remembers.
  Approves a draft.
- `EnsureExpenseAccountAsync` — chart mirror by number, else Holded's live `expenses-accounts` list,
  else POST; the chart mirror is refreshed whenever it lacked the number. Returns the account's Holded
  id (what a caller that books to it stores) and whether it was created.
- `DeletePurchaseDocumentAsync` (no caller yet) — finds the document live (contact, description, total, date);
  refuses when its notes differ or it carries a payment; deletes, then runs the trailing ledger sync
  (an older document's lines leave the mirror at the nightly balance check's re-pull).
- `PostJournalEntryAsync` — the client's ledger-entry post behind the face; not idempotent, the
  outcome says whether Holded confirmed it.
- `PayPurchaseDocumentAsync` / `ReconcileBankMovementAsync` — the client's treasury payment and
  bank-line reconcile behind the face, for SEPA booking. The payment returns Holded's payment id
  (or an `unconfirmed:` marker), the one Holded id this face hands out: the reconcile must name it.

Id-keyed writes for callers that already store Holded's ids, one step each:
`CreateContactAsync` / `DeleteContactAsync` (each refreshes the contact mirror; a failed refresh is
logged, the write stands), `CreatePurchaseDocumentAsync`, `UploadPurchaseAttachmentAsync`,
`EnsurePurchaseDocumentApprovedAsync`, `DeletePurchaseDocumentByIdAsync`, and for sales documents
`FindSalesDocumentsByTagAsync` (both kinds, read live: the issuance guard), `IssueSalesDocumentAsync`
(create, approve, read back; each line's `AccountNum` resolved through the chart mirror, an unknown
number refused) and `ApproveSalesDocumentAsync`.

`IHoldedClient` is internal, so no other section can call it: reads come from the mirrors, a caller that needs fresh data
runs the mirror's sync first (`SyncContactsAsync`, `SyncPurchaseDocumentsAsync`,
`SyncPurchaseDocumentNotesAsync` for named documents, `SyncChartAsync`),
and each mirror reports its last sync. Audit entries stay with the calling section, which knows why
it wrote.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Finance admin / Admin | Everything above (`FinancePolicies.Manage`). |
| Board | Read the summary, accounts, statements, entries, purchases and calls; trigger no sync (`FinancePolicies.Read`). |
| Other sections | Read via `IHoldedService`; write via `IHoldedWrite`; trigger sync (nightly job). |
| Any human | None directly. |

## Invariants

- The Accounts controller orders PGC groups by leading digit and each group's accounts by number. Service grouping, filters and totals do not impose display order; summary counts are independent of row order.

- The summary controller orders sync-state rows by kind using ordinal comparison, and the calls controller orders Madrid month buckets oldest first (the pivot's columns). Services supply the state and aggregated counts without display ordering.

- `GetChartAsync` returns mirror account metadata, including archived accounts, without display ordering. Workgroups' account pickers order by number in their views. `SyncChartAsync` refreshes it in one list call (the ledger sweep does too); `GetChartSyncedAtAsync` says when.

- Operator timestamps and ledger dates use the shared display formatters in Europe/Madrid time; monthly usage labels use the shared month/year formatter. Date ordering and month names follow the UI culture, and missing sync stamps still read "never".

- Nightly sweep = one trailing **45-day** window anchored on *now* (accounting-date filter ⇒
  never anchor on the newest cached line); full sweep (inception → today) on cold cache or the
  Full sync button. Reconciliation catches anything older, so correctness does not depend on
  window width — the window is a quota choice.
- Purchase-document sync is a full pull with replace semantics (Holded's purchase list has no
  reliable incremental key). Notes need one GET per doc: only docs the mirror has no notes for,
  newest first, at most 50 a run; a transient failure ends that run's fetching and never fails the
  sync. A failed list records `Error` on the `PurchaseDocs` state and keeps the last good mirror.
- Each sync kind is serialized by its own non-blocking in-process gate; a second caller is
  skipped and told so, never queued (single-server deployment).
- `SyncAccountLedgerAsync(accountNum)` re-pulls one account's whole history (inception to today) and replaces
  only that account's mirrored lines, under the same gate (false when a sweep holds it). Sync states are
  untouched. Finance's Pleo and SEPA bookings and Expenses' report publishing call it before posting onto
  an account, so their "already in Holded" judgement is Holded's own current ledger.
- Reads (`IHoldedService`: lines, balances, entry, purchase lines, open document amounts, sums by account,
  chart, purchase documents, contacts, bank movements, last ledger sync) never call Holded, and nothing loads the whole ledger table: balances and sums are grouped
  in the database, statements load only their own entries. Amounts are Holded's raw sign.
- `GetLedgerSyncedAtAsync` is the later `LastSyncAt` of the `Ledger` and `FullSync` states, stamped only
  on success, so a failed sweep leaves the last good one. Finance's expense recon uses it to tell a push
  the ledger has not swept yet (Pending) from one that is absent (Missing).
- `GetOpenDocumentAmountsAsync` is Σ (credit − debit) over `purchase`, `purchaserefund` and `payment`
  lines per account: what documents leave open, not the balance (hand-booked entries and collections
  are left out).
- A ledger line keeps Holded's `id` as `EntryId` (the journal entry's id, shared by every line of
  the entry, so never a key: the natural key stays `(EntryNumber, Line)`) and `checked` as
  `Checked` (Holded's manual tick, not bank reconciliation; null until the line is re-synced).
  `doc_description` and `tags` are not stored.
- API-call metering drains honour cancellation before consuming the buffer, then persist consumed records independently of request cancellation. Overview reads remain cancellable afterward.
- The treasury bank feed is mirrored per window: `SyncBankMovementsAsync(treasury, from, to)` reads
  Holded's feed for that window and replaces it in `holded_bank_movements`; `GetBankMovementsAsync`
  reads the mirror with the `BankMovements` state's `LastSyncAt` (null until a sync succeeds). A
  sync already running answers false, a failed one throws and keeps the last good stamp. Finance's
  SEPA booking syncs before every read it moves money on; `/Finance/Sepa` reads the mirror alone.
  The feed's parsing rules are in [`Holded-connector.md`](Holded-connector.md).
- Nothing here is keyed by member, so this section holds no consent gate and no erasure path.
  It is not free of personal data: a creditor account's name is the member's and the lines on it
  are their reimbursement history, identifiable through Finance's member→creditor binding.
  Finance's GDPR contributor exports those lines (its `CreditorLedger` slice, read through
  `IHoldedService.GetLedgerLinesAsync`) and declares their fiscal-retention basis.
  The contact mirror holds the same personal data Holded's contact list does (a member's name,
  IBAN, email, phone, address). It is keyed by Holded's contact id, not by member, follows Holded
  on every sync, and Finance's creditor statement and payout already read it through the binding.
  Finance's contributor exports each bound contact's row (its `HoldedCreditorContacts` slice, IBAN
  masked) and declares fiscal retention for it: the row mirrors Holded's counterparty record.

## Negative Access Rules

- Only this section's repository touches `HoldedDbContext`.
- This section never reads Finance's tables and calls no Finance service. Its only Finance
  reference is `FinancePolicies`, the policy names its pages are gated by.

## Triggers

- `HoldedSyncJob` (nightly at 03:00, this section's `Jobs/`; scheduled by this section's `ISectionJobs`, job id `holded-sync`): `SyncPurchaseDocumentsAsync`, `SyncContactsAsync`, then `SyncLedgerAsync(full: false)`, under the `holded-api` Hangfire job lock so a long run never overlaps the next. A failed document or contact pull still lets the ledger sweep run, then fails the job.
- `HoldedMirrorCatchUp` (hosted service): at boot, with an API key and a purchase-document or contact mirror that has never synced (a fresh deployment), enqueues one `HoldedSyncJob` instead of leaving Finance's actuals empty until 03:00. Catch-up read or enqueue failures log a warning and let the app boot; scheduled and manual sync remain available. Requested startup cancellation propagates.
- `/Holded` buttons: `SyncNow` (purchase docs, contacts, then incremental ledger + reconcile; each runs whatever the other did, and a failure names which), `FullSync` (ledger).
- Finance's `POST /Finance/HoldedSync/Run` ("Sync Now" on its unmatched queue) calls `SyncPurchaseDocumentsAsync`.

## Cross-Section Dependencies

- Outbound: Finance's `FinancePolicies` constants only (`Humans.Finance.Contracts`).
- Inbound:
  - Finance: every mirror and its sync through `IHoldedService` (creditors, actuals, recon, SEPA,
    Pleo), and through `IHoldedWrite` creditor contacts, expense accounts, contact and duplicate
    deletes, payments, bank-line reconciles and journal entries.
  - Expenses: the contact mirror, the account re-pull, creditor contacts and the purchase-document
    create, attach and approve writes.
  - Store: the chart mirror, contact creation and the sales-document writes.
  - Workgroups: the chart mirror for the expense-account picker.
  - Backdoor: the purchase-document mirror for the finance read API.

## Architecture

**Owning section:** `Holded` (`src/Sections/Humans.Holded`)
**Public contract:** `Humans.Holded.Contracts` (the `Contracts/` folder): `IHoldedService` (+ `HoldedLedgerLineInfo`, `HoldedChartAccountInfo`, `HoldedPurchaseDocInfo`, `HoldedPurchaseDocMirror`, `HoldedContactMirror`, `HoldedBankMovementMirror`) for the mirrors, `IHoldedWrite` (+ its outcome records) for writes, and the DTOs both carry. The connector itself, `IHoldedClient` (`Services/`), is internal (see [`Holded-connector.md`](Holded-connector.md))

**Inside:** `Service` serves the reads and the admin screen and forwards each sync to `HoldedMirrorSync`,
which owns the gates, replacement, reconciliation, failure state and call-log drain. `HoldedAccountReader`
builds balances, account rows, statements and entries. `HoldedWrite` is the write face. The connector is
described in [`Holded-connector.md`](Holded-connector.md).
**Owned tables:** those above
**Target shape:** [`health.md`](health.md). Historical design record:
[`2026-08-10-holded-v2-migration-design.md`](2026-08-10-holded-v2-migration-design.md).
