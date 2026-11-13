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

It is the association's only line to its accounting vendor. Nothing else in the system talks to
Holded; everything else asks this section.

Its jobs:

- **Keeps copies.** The book (every journal line and the chart of accounts), every purchase
  document, every contact, and the bank feed of the windows someone asked for are copied locally,
  so a page never waits on Holded and a balance never costs a call. Every night it refreshes the
  documents, the contacts and the recent part of the book, then checks its own arithmetic against
  Holded's account totals and re-reads whatever does not agree.
- **Writes for others.** Other parts of the system decide; this section books supplier documents
  and their attachments, pays them, ticks bank lines as settled, posts journal entries, issues
  customer invoices and receipts, and creates or removes contacts and expense accounts. It hands
  back outcomes, not Holded's internal ids, except the one payment id a later bank tick must name.

Every call is counted, because the plan allows only so many a month. A finance admin (and the
Board, read-only) gets one screen: the month's call usage, when each copy last refreshed, which
accounts disagree with Holded, the chart, the purchase documents, and any account's or entry's
lines with who the money moved to or from.

## 2. The shapes

| # | Question the caller is asking | Answered by | Members |
|---|---|---|---|
| 1 | *What does the book say?* | `IHoldedService` | `GetLedgerLinesAsync`, `GetAccountBalancesAsync`, `GetEntryAsync`, `GetLedgerSyncedAtAsync` |
| 2 | *What accounts exist to book to?* | `IHoldedService` | `GetChartAsync`, `SyncChartAsync` |
| 3 | *What documents, contacts and bank lines does Holded hold?* | `IHoldedService` | `GetPurchaseDocumentsAsync`, `GetContactsAsync`, `GetBankMovementsAsync` |
| 4 | *Make a copy current before I act on it.* | `IHoldedService` | `SyncLedgerAsync`, `SyncAccountLedgerAsync`, `SyncPurchaseDocumentsAsync`, `SyncPurchaseDocumentNotesAsync`, `SyncContactsAsync`, `SyncBankMovementsAsync` |
| 5 | *Make every copy current tonight.* | `IHoldedNightlySync` + `HoldedSyncJob`, `HoldedMirrorCatchUp` | `RunAsync` |
| 6 | *Book a cost we owe.* | `IHoldedWrite` | `CreatePurchaseDocumentAsync`, `UploadPurchaseAttachmentAsync`, `EnsurePurchaseDocumentApprovedAsync`, `DeletePurchaseDocumentByIdAsync`, `EnsureExpenseAccountAsync` |
| 7 | *Record that money moved.* | `IHoldedWrite` | `PayPurchaseDocumentAsync`, `ReconcileBankMovementAsync`, `PostJournalEntryAsync` |
| 8 | *Book money we are owed.* | `IHoldedWrite` | `FindSalesDocumentsByTagAsync`, `IssueSalesDocumentAsync`, `ApproveSalesDocumentAsync` |
| 9 | *Who is the counterparty?* | `IHoldedWrite` | `EnsureCreditorContactAsync`, `CreateContactAsync`, `DeleteContactAsync` |
| 10 | *Show the copies and the meter.* | `IHoldedAdminService` + `/Holded/*` | `GetSummaryAsync`, `GetAccountsPageAsync`, `GetAccountStatementAsync`, `GetEntryViewAsync`, `GetCallsByMonthAsync` |
| 11 | *Can we call Holded at all?* | `IHoldedService` | `IsConfigured` |

Shapes 1–5 are the mirror, 6–9 the write face, 10 the admin screen. Every shape sits on the
internal connector, `IHoldedClient`, which no other section can reach.

Built with no caller yet (the single-mirror phase 1 added them ahead of phase 2's adopters):
`IHoldedWrite.EnsurePurchaseDocumentAsync`, `IHoldedWrite.DeletePurchaseDocumentAsync` (the
match-and-refuse delete), `IHoldedService.GetPurchaseLinesAsync`,
`IHoldedService.GetOpenDocumentAmountsAsync`, `IHoldedService.GetSumsByAccountAsync` and
`IHoldedService.GetChartSyncedAtAsync`. They are surface without a shape until a caller arrives.

## 3. Structure

Written fresh from the shapes, not from today's folders.

```
Contracts/                      what other sections may hold
  IHoldedService                shapes 1–4 and 11: mirror reads and the syncs a caller may run first
  IHoldedWrite                  shapes 6–9: every write another section makes, plus its outcome records
  mirror and write DTOs         what those interfaces carry; nothing else
  HoldedApiException            transient vs permanent, the one failure vocabulary callers catch
Services/Service                IHoldedService reads + IHoldedAdminService; forwards syncs
Services/HoldedMirrorSync       every sync: gates, replace, reconcile, failure state, call-log drain
Services/HoldedAccountReader    balances, account rows, statements and entries for the screen
Services/HoldedWrite            the write face over the connector and the chart mirror
Services/IHoldedClient          the connector, internal
Services/HoldedClient           facade over the operation clients
Services/Holded*Client          purchases, sales, contacts, accounting, treasury: payloads and parsing
Services/HoldedTransport        auth, 429 policy, cursor paging, metering, timing
Services/HoldedJson             wire-value readers and the null-omitting serializer options
Services/HoldedCallLog          in-process call meter, drained to holded_api_calls
Services/HoldedNightlySync      shape 5's body
Jobs/                           the Hangfire shim and the boot catch-up
Data/Repository                 the only code that touches HoldedDbContext
Domain/                         the mirrored tables, internal
Controllers/HoldedController    translation and display order only
Models/HoldedAdminModels        the screen's vocabulary, internal
Views/Holded/                   read-only pages plus the sync buttons
```

Structural facts the layout should make obvious:

- **The connector is a leaf.** `IHoldedClient` holds no table and no mirror invariant; it is
  internal so the compiler, not review, keeps every other section off Holded's API.
- **Connector-only DTOs do not belong in `Contracts/`.** The usage, expense-account, purchase
  read and payment-detail DTOs and `HoldedClientOptions` cross no section boundary; their place
  in the public folder is debt, not design (`Docs/debt.yml`).
- **`Models/HoldedAdminModels.cs` stays internal.** One escaped type makes the screen a contract.

## 4. Invariants

Stated so a violation is recognisable; each names the line that enforces it.

1. **A sweep is the truth for its window.** After `ReplaceLedgerWindowAsync` the mirror holds
   exactly the fetched rows inside the window (optionally one account); an empty fetch deletes,
   it does not no-op. `src/Sections/Humans.Holded/Data/Repository.cs:53`
2. **The trailing window is anchored on now**, never on the newest cached line: the API filters
   on accounting date, so a late entry dated into a closed month must still be reachable.
   `src/Sections/Humans.Holded/Services/HoldedMirrorSync.cs:88`
3. **Mirror reads never call Holded.** `Service`'s read members reach only the repository.
   `src/Sections/Humans.Holded/Services/Service.cs:35`
4. **Reconciliation compares the raw `Debit − Credit` sign**; the association's-POV flip is
   applied after the verdict and only for display.
   `src/Sections/Humans.Holded/Services/HoldedMirrorSync.cs:403`,
   `src/Sections/Humans.Holded/Services/HoldedAccountReader.cs:159`
5. **One sync of a kind at a time, and the loser is told.** Each gate is non-blocking; a second
   caller gets `false`, never a queue. `src/Sections/Humans.Holded/Services/HoldedMirrorSync.cs:68`
6. **A short read never becomes a delete.** A page without `items`, a null item, `has_more`
   without a cursor, a repeated cursor or the page cap throws; the prefix is never returned.
   `src/Sections/Humans.Holded/Services/HoldedTransport.cs:71`
7. **A missing identity or amount fails the page**, never a manufactured `0`.
   `src/Sections/Humans.Holded/Services/HoldedJson.cs:49`
8. **No payload clears a Holded field it was not asked to clear**: every write serializes with
   nulls omitted. `src/Sections/Humans.Holded/Services/HoldedJson.cs:16`
9. **A failed sync keeps the last good copy and stamp**: the error path writes status, message
   and time, never `LastSyncAt`. `src/Sections/Humans.Holded/Services/HoldedMirrorSync.cs:462`
10. **A posted payment never throws**: once the POST may have landed, an unreadable id comes back
    as `unconfirmed:{documentId}`. `src/Sections/Humans.Holded/Services/HoldedPurchasesClient.cs:209`
11. **Contacts are created, never updated**: v2's PUT is a full replacement that mints a second
    creditor account. `src/Sections/Humans.Holded/Services/HoldedContactsClient.cs:31`
12. **Only `Repository` touches `HoldedDbContext`.**
    `src/Humans.Analyzers/Internal/Rules/DbContextOwnershipRule.cs:8`

### Negative access

- `/Holded/*` reads need `FinancePolicies.Read` (Finance Admin, Board, Admin); the sync
  POSTs need `FinancePolicies.Manage` (never Board) and an antiforgery token.
  `src/Sections/Humans.Holded/Controllers/HoldedController.cs:13`,
  `src/Sections/Humans.Holded/Controllers/HoldedController.cs:80`
- No other section can call Holded's API: the connector is internal.
  `src/Sections/Humans.Holded/Services/IHoldedClient.cs:6`

### Personal data

Nothing is keyed by member, so there is no consent gate and no erasure path here. The contact
mirror and creditor-account lines are personal data reachable through Finance's member→creditor
binding; Finance's GDPR contributor exports them.

## 5. Seams

Specified-but-unbuilt. Not to be built by a doctor run.

- **The connector as its own section.** It has no table and no mirror invariant; splitting it
  would leave a small mirror section. Nothing has decided this.
- **The uncalled members in §2.** Either phase 2's adopters arrive or the members go; Peter's call.

## 6. Deliberately not done

- **No caching decorator.** Every read is already a mirror read.
- **No retry library.** One 429 retry, content-free GETs only: a write cannot be replayed without
  knowing whether Holded applied it.
- **No per-endpoint client interfaces.** The operation clients split the code; one internal
  interface is the seam tests fake.
- **No contact update**, for invariant 11's reason.
- **No Debit/Credit columns on any `/Holded` page.** One signed amount in the association's POV.
- **No localization.** `/Holded` is a finance-admin screen.

## Load-bearing weirdness

Settled decisions. Later runs should stop re-litigating these.

- **`holded_accounts` is the migration sentinel, not `holded_ledger_lines`**: the ledger table
  predates the section split, so it cannot prove this baseline ran.
- **`HOLDED_API_KEY_V2`, not `HOLDED_API_KEY`**: the v1 API rejects a v2 key.
- **The call budget is config, not Holded's `limit`**: `GET /usage`'s `limit` is the
  billable-overage ceiling, not the plan allowance.
- **PGC group names come from the account number's leading digit**, never Holded's free-text
  Spanish `group`.
- **A standing reconciliation mismatch is reportable state, not a failure**: Holded's chart
  totals exclude unconfirmed entries.
- **The 45-day window is a quota choice**: reconciliation catches anything older.
- **The payment id is read off the document, not the POST**, which answers `{"status":"paid"}`
  and nothing else.
- **`HoldedMirrorSync` is built per call** and its gates are static: the gates, not the
  instance, carry the one-at-a-time rule.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-03 | First doctoring: the nightly sweep could skip in silence and a numberless account became account 0; the general-ledger page's own summary asserted the opposite sign convention to the one it renders | peterdrier/Humans#1583 |
| 2 | 2026-10-28 | The docs still called the GDPR export of mirrored creditor lines undecided after Finance shipped it; connector rules lived in two docs; a dead list field and a create-only "upsert" | peterdrier/Humans#2177 |
| 3 | 2026-11-13 | The docs narrated the single-mirror refactor instead of stating its rules and listed a fraction of who calls what; doc comments contradicted their code; contract members have never had a caller | peterdrier/Humans#2411 |
