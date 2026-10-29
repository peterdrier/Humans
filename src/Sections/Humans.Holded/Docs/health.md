<!-- freshness:triggers
  src/Sections/Humans.Holded/**
  src/Sections/Humans.Holded/Contracts/**
  src/Sections/Humans.Expenses/Jobs/HoldedExpenseOutboxJob.cs
-->

# Holded — target shape

Regenerated every section-doctor run, before any scan. This is the shape the section is aiming
at, derived from what it does; `Holded.md` is the shape it has. Where they differ, the difference
is the work.

## 1. What the section does

Keeps a local copy of the association's accounting book so nothing else has to ask Holded for it,
and owns the one piece of software that talks to Holded at all.

The section is not one job:

- **The book-keeper's copy.** Every night it asks Holded what happened in the daybook lately,
  writes that down, and then checks its own arithmetic against Holded's account totals. Where the
  two disagree it re-reads that account's whole history once and reports what still will not
  reconcile. Nothing else in the system ever waits on Holded to show a balance.
- **The telephone to Holded.** Creating supplier invoices, paying them, issuing customer invoices
  and receipts, keeping counterparty records, listing the chart of accounts. Other parts of the
  system do the deciding; this section does the talking, and counts the calls because the plan
  only allows so many a month.

It also shows a finance admin one screen: how much of the month's call allowance is gone, when
each sync last ran, which accounts do not reconcile, and — for any account — every line on it and
who the money moved to or from.

## 2. The shapes

| # | Question the caller is asking | Answered by | Members |
|---|---|---|---|
| 1 | *What does the book say about this account?* | `IHoldedService` (mirror reads) | `GetLedgerLinesAsync`, `GetAccountBalancesAsync` |
| 2 | *Make the copy current.* | `IHoldedService` / `IHoldedNightlySync` | `SyncLedgerAsync`, `RunAsync` |
| 3 | *Show me the mirror's own state.* | `IHoldedAdminService` + `GET /Holded` | `GetOverviewAsync` |
| 4 | *Show me one slice of the book.* | `IHoldedAdminService` + `GET /Holded/Accounts/{n}`, `GET /Holded/Entries/{n}` | `GetAccountStatementAsync`, `GetEntryAsync` |
| 5 | *Book a cost we owe.* | `IHoldedClient` | `CreatePurchaseDocumentAsync`, `ApprovePurchaseDocumentAsync`, `DeletePurchaseDocumentAsync`, `UploadAttachmentAsync` |
| 6 | *Book money we are owed.* | `IHoldedClient` | `CreateSalesDocumentAsync`, `ApproveSalesDocumentAsync` |
| 7 | *Read a document back.* | `IHoldedClient` | `GetPurchaseDocumentAsync`, `GetSalesDocumentAsync`, `ListPurchaseDocumentsAsync`, `FindSalesDocumentIdsByTagAsync` |
| 8 | *Record that money moved.* | `IHoldedClient` | `PayPurchaseDocumentAsync`, `PostLedgerEntryAsync` |
| 9 | *What did the bank see, and which documents does a line settle?* | `IHoldedClient` | `ListBankMovementsAsync`, `ReconcileBankMovementAsync` |
| 10 | *Who is the counterparty?* | `IHoldedClient` | `CreateContactAsync`, `GetContactAsync`, `ListContactsAsync`, `DeleteContactAsync` |
| 11 | *What accounts exist to book to?* | `IHoldedClient` | `ListAccountingAccountsAsync`, `ListExpenseAccountsAsync`, `CreateExpenseAccountAsync` |
| 12 | *What raw journal lines are in this window?* | `IHoldedClient` | `ListLedgerEntriesAsync` |
| 13 | *Can we call Holded, and how much have we spent?* | `IHoldedClient` | `IsConfigured`, `GetUsageAsync` |

Shapes 1–4 are the mirror; 5–13 are the connector. They share a project and nothing else: the
mirror is the connector's only in-section caller (shape 12 and part of 11 and 13), and shapes 5–10
exist for Finance, Expenses, Store and Workgroups. Shape 9 is the one connector read that is deliberately not
mirrored: Finance's SEPA booking reads the bank feed live over a bounded window. That is the
section's one real seam.

## 3. Structure

Written fresh from the shapes, not from today's folders.

```
Contracts/                    what other sections may hold
  IHoldedClient + its DTOs      shapes 5–13 — the telephone
  IHoldedService                shapes 1–2 — the mirror's cross-section read
  HoldedApiException            the connector's transient-vs-permanent failure classification
Services/HoldedClient           the existing typed-client contract facade; composes the operation clients
Services/Holded*Client          purchases, sales, contacts, accounting and treasury: payloads and parsing
Services/HoldedTransport        one shared retry, auth, timing, metering and cursor-pagination pipeline
Services/HoldedJson             shared wire-value readers and null-omitting serialization options
Services/Service                shapes 1–4: sweep, reconcile, and the read models
Services/HoldedCallLog          the in-process meter the client fills and Service drains
Services/HoldedNightlySync      shape 2's body: this section's sweep (Finance runs its own doc sync)
Services/IHoldedNightlySync     shape 2 — the nightly body, internal: only the job calls it
Jobs/HoldedSyncJob              the Hangfire shim over it; SectionJobs names it and the cron
Data/Repository                 the only code that touches HoldedDbContext
Domain/                         the mirrored tables; internal, and they stay internal
Controllers/HoldedController    translation only, no arithmetic
Models/HoldedAdminModels        the admin screen's vocabulary, every type internal
Views/Holded/                   all read-only, all admin
```

Structural facts the layout should make obvious and currently does not:

- **`IHoldedClient` is a leaf, not a section service.** It has no repository, no invariant of the
  mirror's, and never touches `HoldedDbContext`. It lives in `Contracts` because Expenses, Finance,
  Store and Workgroups call it directly.
- **`Models/HoldedAdminModels.cs` must stay internal.** The moment one type escapes, the admin
  screen becomes a cross-section contract.

## 4. Invariants

Stated so a violation is recognisable; each names the line that enforces it.

1. **A sweep is the truth for its window.** After `ReplaceLedgerWindowAsync(from, to, account,
   rows)` the mirror holds exactly `rows` inside `[from, to]` for that account — an empty `rows`
   deletes, it does not no-op. `src/Sections/Humans.Holded/Data/Repository.cs:51`
2. **The sweep window is anchored on now**, never on the newest cached line: the API filters on
   accounting date, so a late-posted entry dated into a closed month must still be reachable.
   `src/Sections/Humans.Holded/Services/Service.cs:76`
3. **Reads never call Holded.** `GetLedgerLinesAsync`, `GetAccountBalancesAsync`,
   `GetAccountStatementAsync` and `GetEntryAsync` touch the repository only.
   `src/Sections/Humans.Holded/Services/Service.cs:41`
4. **Reconciliation compares the raw `Debit − Credit` convention**, before any display flip.
   A page shows the association's POV; the ✓/✗ never does.
   `src/Sections/Humans.Holded/Services/Service.cs:380`
5. **One sweep at a time, and the loser is told.** The gate is non-blocking; `SyncLedgerAsync`
   returns `false` rather than queueing, and every caller reports that distinctly from success.
   `src/Sections/Humans.Holded/Services/Service.cs:56`
6. **A short read is never allowed to become a delete.** Pagination that cannot be completed —
   a missing `items` array, `has_more` with no cursor, the page cap — throws; it never returns
   the prefix it has. `HoldedTransport.GetPagedAsync`
7. **An identity or amount field Holded did not send fails the page.** No manufactured `0`
   reaches the mirror: on a ledger window a fabricated line overwrites a real cached one; on the
   chart, whose upsert is additive, it creates a phantom account instead.
   `HoldedJson.ReadRequiredInt` / `ReadRequiredString` / `ReadRequiredDecimalV2`
8. **The connector never clears a Holded field it was not asked to clear** — every payload omits
   its nulls. `HoldedJson.OmitNulls`
9. **Only `Repository` touches `HoldedDbContext`**, and this section reads no other section's
   tables — Finance's doc-sync row arrives through `IHoldedFinanceService`, in the controller.
   `src/Humans.Analyzers/Internal/Rules/DbContextOwnershipRule.cs:8`

### Negative access

- `/Holded/*` is `FinanceAdmin` or `Admin` only — every route, the POSTs included, and those
  additionally require the antiforgery token.
  `src/Sections/Humans.Holded/Controllers/HoldedController.cs:13`
- Nothing outside this section may hold a `HoldedDbContext`, a `HoldedLedgerLine` or any other
  `Domain/` entity; `HoldedLedgerLineInfo` is the only ledger shape that crosses the boundary.
  `src/Sections/Humans.Holded/Domain/HoldedLedgerLine.cs:9`

### Personal data

Nothing here is keyed by member, so the section holds no consent gate and no erasure path of its
own. It is not free of personal data: a creditor account's name is the member's and the lines on
it are their reimbursement history. Finance's GDPR contributor exports those lines as its
`CreditorLedger` slice, read through `IHoldedService.GetLedgerLinesAsync`
(`src/Sections/Humans.Finance/Services/Service.cs:783`). Not a licence to add member-linked
columns here.

## 5. Seams

Specified-but-unbuilt. Not to be built by a doctor run; recorded because items touching these
callers are shaped by them.

- **The connector wants to be its own section.** It is the whole of shapes 5–13, has no table,
  no invariant of the mirror's, and its cross-section callers (Finance, Expenses, Store, Workgroups) do not
  care about the ledger at all. Splitting it would leave `Humans.Holded` a genuinely small
  mirror. Nothing has decided this; the cost is a project split plus a rename across every
  calling section.
- **The `/Holded` and `/Finance/Holded` pair.** Both screens cover one integration, split by table
  ownership rather than by what an admin is trying to find out
  (nobodies-collective/Humans#1000). `/Finance/Holded` links to `/Holded`; `/Holded` links to
  Finance's creditor pages but not back to `/Finance/Holded`. Whether one page with two sections
  is the honest shape is undecided.
- **A bank-movement mirror.** If a reconciliation panel ever needs the bank feed without a live
  call, a bank-movements mirror table would land here (design record).

## 6. Deliberately not done

- **No caching decorator.** Every read is already a mirror read; a second cache in front of the
  first buys nothing and adds an invalidation problem.
- **No retry/backoff library.** One 429 retry, GETs only, is the whole policy — a content-bearing
  request cannot be replayed without knowing whether Holded applied it, and that judgment does not
  generalise into a policy object.
- **No per-endpoint client interfaces.** `IHoldedClient` is wide because the Holded API is wide.
  Splitting it by shape group would multiply DI registrations and test doubles to hide a length
  no reader is confused by.
- **No contact update.** v2's contact PUT is a full replacement that resets `supplier_record`
  and mints a second creditor account, so the connector only creates contacts.
- **No `Debit`/`Credit` columns on any `/Holded` page.** Peter's call: a single signed amount in
  the association's POV is the thing a reader can act on. Bookkeeping sign lives in the data and
  in reconciliation, never on screen.
- **No abstraction over the sales-document kinds** beyond the path segment. They differ in
  one string; a strategy would be a class per string.

## Load-bearing weirdness

Settled decisions. Later runs should stop re-litigating these.

- **`holded_accounts` is the migration sentinel, not `holded_ledger_lines`.** The ledger table
  predates the section split — the historical chain and pre-split Finance both created it — so it
  cannot prove this baseline ran. Documented in `Section.cs`; do not "simplify" it.
- **`HOLDED_API_KEY_V2`, not `HOLDED_API_KEY`.** A v2 key is rejected by the v1 API, so the names
  must differ while both builds can coexist.
- **The call budget is config, not Holded's `limit`.** `GET /usage`'s `limit` is the
  billable-overage ceiling (2,000,000), not the plan allowance (~2,000). The screen shows both on
  purpose.
- **PGC group names come from the account number's leading digit**, never from Holded's own
  `group` string, which is free-text Spanish.
- **`ToAssociationPov` flips groups 6–9 and nothing else**, and it is display-only.
- **A standing reconciliation mismatch is reportable state, not a failure.** Holded's own chart
  totals exclude unconfirmed entries, so some accounts legitimately never reconcile.
- **The 45-day window is a quota choice, not a correctness one.** Reconciliation is what catches
  anything older; widening the window buys nothing and costs calls.
- **`PayPurchaseDocumentAsync` reads the payment id off the document, not the POST.** The POST
  answers `{"status":"paid"}` and nothing else (verified live 2026-10-18), so the id is the
  `payments_detail` entry that is new after the POST and matches amount, date and treasury
  account. When the re-read fails or no single entry matches it returns `unconfirmed:{docId}`
  rather than throwing: the money moved, and losing the reference would cause a second payment.
- **`ListContactsAsync` skips one unreadable contact; every other list fails the whole page.**
  A missing contact name is cosmetic; a missing ledger line or purchase total is wrong money.
  The chart fails the page as well — Peter's ruling of 2026-09-10 against skip-and-log
  (peterdrier/Humans#1583, item 1). Holded documents an account's `number` as a non-nullable
  integer and a live read of the chart returned none without one, so the numberless account is a
  test fixture rather than an observed incident. The cost of skipping is the decisive part:
  `ListAccountingAccountsAsync` runs first in `RefreshAccountsAndReconcileAsync`, so a skip would
  let that pass report a completed refresh *and* reconciliation having silently dropped an
  account. A blocked nightly is the louder failure, and the one we keep. `UpsertAccountsAsync`
  being additive — it never deletes an omitted account — argues the other way and did not carry it.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-03 | First doctoring: the nightly sweep could skip in silence and a numberless account became account 0; the general-ledger page's own summary asserted the opposite sign convention to the one it renders | peterdrier/Humans#1583 |
| 2 | 2026-10-28 | The docs still called the GDPR export of mirrored creditor lines undecided after Finance shipped it; connector rules lived in two docs; a dead list field and a create-only "upsert" | peterdrier/Humans#2177 |
