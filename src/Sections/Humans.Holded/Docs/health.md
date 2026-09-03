<!-- freshness:triggers
  src/Sections/Humans.Holded/**
  src/Sections/Humans.Holded.Contracts/**
  src/Sections/Humans.Expenses/Jobs/HoldedExpenseOutboxJob.cs
-->

# Holded — target shape

Regenerated every section-doctor run, before any scan. This is the shape the section is aiming
at, derived from what it does; `Holded.md` is the shape it has. Where they differ, the difference
is the work.

## 1. What the section does

Keeps a local copy of the association's accounting book so nothing else has to ask Holded for it,
and owns the one piece of software that talks to Holded at all.

Two jobs, not one:

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
| 5 | *Book a cost we owe.* | `IHoldedClient` | `CreatePurchaseDocumentAsync`, `ApprovePurchaseDocumentAsync`, `UploadAttachmentAsync` |
| 6 | *Book money we are owed.* | `IHoldedClient` | `CreateSalesDocumentAsync`, `ApproveSalesDocumentAsync` |
| 7 | *Read a document back.* | `IHoldedClient` | `GetPurchaseDocumentAsync`, `GetSalesDocumentAsync`, `ListPurchaseDocumentsAsync`, `FindSalesDocumentIdsByTagAsync` |
| 8 | *Record that we paid.* | `IHoldedClient` | `PayPurchaseDocumentAsync` |
| 9 | *Who is the counterparty?* | `IHoldedClient` | `UpsertContactAsync`, `GetContactAsync`, `ListContactsAsync` |
| 10 | *What accounts exist to book to?* | `IHoldedClient` | `ListAccountingAccountsAsync`, `ListExpenseAccountsAsync`, `CreateExpenseAccountAsync` |
| 11 | *What raw journal lines are in this window?* | `IHoldedClient` | `ListLedgerEntriesAsync` |
| 12 | *Can we call Holded, and how much have we spent?* | `IHoldedClient` | `IsConfigured`, `GetUsageAsync` |

Shapes 1–4 are the mirror; 5–12 are the connector. They share a project and nothing else: the
mirror is the connector's only in-section caller (shape 11 and part of 10 and 12), and shapes 5–9
exist solely for Finance, Expenses and Store. That is the section's one real seam.

## 3. Structure

Written fresh from the shapes, not from today's folders.

```
Humans.Holded.Contracts/      the two things other sections may hold
  IHoldedClient + its DTOs      shapes 5–12 — the telephone
  IHoldedService                shapes 1–2 — the mirror's cross-section read
  IHoldedNightlySync            shape 2 — the nightly body
  HoldedApiException            the connector's two-way failure classification
Humans.Holded/
  Services/HoldedClient         one class per shape group would be smaller, but the retry,
                                auth, metering and paging machinery is shared by all of them —
                                so: one class, its private helpers, and nothing else
  Services/Service              shapes 1–4: sweep, reconcile, and the three read models
  Services/HoldedCallLog        the in-process meter the client fills and Service drains
  Services/HoldedNightlySync    shape 2's body: Finance's doc sync, then this section's sweep
  Jobs/HoldedSyncJob            the Hangfire shim over it — public only because Hangfire needs
                                the concrete type; SectionJobs names it and the cron
  Data/Repository               the only code that touches HoldedDbContext
  Data/Domain/                  the four mirrored tables; internal, and they stay internal
  Controllers/HoldedController  translation only: five routes, no arithmetic
  Views/Holded/                 three pages, all read-only, all admin
```

Two structural facts the layout should make obvious and currently does not:

- **`IHoldedClient` is a leaf, not a section service.** It has no repository, no invariant, and
  never touches `HoldedDbContext`. It lives in `Contracts` because Expenses, Finance and Store
  call it directly. Nothing about the mirror is required to understand it.
- **`Models/HoldedAdminModels.cs` is the view layer's vocabulary**, one file for shapes 3–4, and
  every type in it is `internal`. It must stay that way: the moment one escapes, the admin screen
  becomes a cross-section contract.

## 4. Invariants

Stated so a violation is recognisable.

1. **A sweep is the truth for its window.** After `ReplaceLedgerWindowAsync(from, to, account,
   rows)` the mirror holds exactly `rows` inside `[from, to]` for that account — an empty `rows`
   deletes, it does not no-op.
2. **The sweep window is anchored on now**, never on the newest cached line: the API filters on
   accounting date, so a late-posted entry dated into a closed month must still be reachable.
3. **Reads never call Holded.** `GetLedgerLinesAsync`, `GetAccountBalancesAsync`,
   `GetAccountStatementAsync` and `GetEntryAsync` touch the repository only.
4. **Reconciliation compares the raw `Debit − Credit` convention**, before any display flip.
   A page shows the association's POV; the ✓/✗ never does.
5. **One sweep at a time, and the loser is told.** The gate is non-blocking; `SyncLedgerAsync`
   returns `false` rather than queueing, and every caller reports that distinctly from success.
6. **A short read is never allowed to become a delete.** Pagination that cannot be completed —
   a missing `items` array, `has_more` with no cursor, the page cap — throws; it never returns
   the prefix it has.
7. **An identity or amount field Holded did not send fails the page.** No manufactured `0`
   reaches the mirror, because replace semantics would then overwrite a real cached line.
8. **The connector never clears a Holded field it was not asked to clear** — every payload omits
   its nulls.
9. **No user-scoped data**, therefore no GDPR contributor and no consent gate. The
   member→creditor binding is Finance's.
10. **Only `Repository` touches `HoldedDbContext`**, and this section reads no other section's
    tables — Finance's doc-sync row arrives through `IHoldedFinanceService`, in the controller.

### Negative access

- `/Holded/*` is `FinanceAdmin` or `Admin` only — every route, including the two POSTs, and the
  POSTs additionally require the antiforgery token.
- Nothing outside this section may hold `IHoldedAdminService`, `HoldedAdminOverview`,
  `HoldedAccountStatement`, `HoldedEntry` or any `Models/` type.
- Nothing outside this section may hold a `HoldedDbContext`, a `HoldedLedgerLine` or any other
  `Domain/` entity; `HoldedLedgerLineInfo` is the only ledger shape that crosses the boundary.

## 5. Seams

Specified-but-unbuilt. Not to be built by a doctor run; recorded because items touching these
callers are shaped by them.

- **The connector wants to be its own section.** It is the whole of shapes 5–12, has no table,
  no invariant of the mirror's, and three cross-section callers who do not care about the ledger
  at all. Splitting it would leave `Humans.Holded` a genuinely small mirror. Nothing has decided
  this; the cost is a project split plus a rename across three sections.
- **The `/Holded` and `/Finance/Holded` pair.** Two screens over one integration, split by table
  ownership rather than by what an admin is trying to find out
  (nobodies-collective/Humans#1000). They link to each other today. Whether one page with two
  sections is the honest shape is undecided.

## 6. Deliberately not done

- **No caching decorator.** Every read is already a mirror read; a second cache in front of the
  first buys nothing and adds an invalidation problem.
- **No retry/backoff library.** One 429 retry, GETs only, is the whole policy — a content-bearing
  request cannot be replayed without knowing whether Holded applied it, and that judgment does not
  generalise into a policy object.
- **No per-endpoint client interfaces.** `IHoldedClient` is wide because the Holded API is wide.
  Splitting it by shape group would multiply DI registrations and test doubles to hide a length
  no reader is confused by.
- **No `Debit`/`Credit` columns on any `/Holded` page.** Peter's call: a single signed amount in
  the association's POV is the thing a reader can act on. Bookkeeping sign lives in the data and
  in reconciliation, never on screen.
- **No abstraction over the two sales-document kinds** beyond the path segment. They differ in
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
- **`PayPurchaseDocumentAsync` returns `unconfirmed:{docId}` rather than throwing** when Holded
  accepts a payment but returns no readable id. The money moved; losing the reference would cause
  a second payment.
- **`ListContactsAsync` skips one unreadable contact; every other list fails the whole page.**
  A missing contact name is cosmetic; a missing ledger line or purchase total is wrong money.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-03 | First doctoring: the nightly sweep could skip in silence and a numberless account became account 0; the general-ledger page's own summary asserted the opposite sign convention to the one it renders | peterdrier/Humans#pending |
