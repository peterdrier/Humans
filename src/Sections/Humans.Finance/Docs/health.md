# Finance — health

**Assessment target.** Derived from the section's behaviour, not from any scan. Regenerated every
section-doctor run and diffed against the previous run's copy.

- Last assessed: 2026-11-02
- Anchor: 3b63d6603
- Previous target: 2026-10-02. Diff: the section moved. New since then: the member's IBAN
  (payment details, with an audited Admin-only reveal), the expense-report reconciliation report
  and its nightly job, the cash-flow page, the expense-vs-ledger match on the creditor pages,
  duplicate-document removal in Holded, withdrawing a cancelled transfer, booking a file's textless
  lines as a set, and the document sync moving in from Holded as its own job. The booking, sync and
  payout-email code left `Service.cs` for their own internal classes. The previous target was also
  wrong in three places: Board reads every page (the read/write split had shipped), Holded's page
  does not read the sync state (only Backdoor does), and the running balance on a ledger line is
  already from the member's side.

## 1. What the section does

Finance is the treasurer's window onto what the organisation actually spent and actually owes its
members, with Holded — the outside bookkeeping system — as the system of record.

**Money out of the org, by budget category.** Every purchase invoice the bookkeeper enters in Holded
is pulled in nightly and attributed to one budget category, so the budget pages can show what a
category really spent against what it planned. An invoice booked to an account Finance manages for
someone other than a category is attributed with no category: off the worklist, outside every
budget. Anything that attributes to nothing lands in a worklist a treasurer works through by hand.

**Getting the accounts to attribute to.** Each budget category needs its own Holded expense account.
A treasurer sees which categories have one and creates the missing ones in one action. Another
section that needs an account with no budget category behind it — a workgroup — asks Finance, which
links an existing account of that name or creates one, and retires or restores it later.

**Planned money over time.** The active budget year's line items, laid out by week or month with the
VAT they carry, against a runway seeded from the year's paid ticket revenue.

**Money the org owes members.** A member who submits an expense report becomes a creditor of the
organisation, with a numbered account in Holded's books. Finance keeps the link between the member
and that account, shows every such account with what is owed on it, and the statement behind any
one of them beside what the member's reports booked to it. The link is made automatically the first
time a member's report is pushed; a treasurer corrects it by hand when that guess is wrong or never
resolved.

**Checking the books against the reports.** Every approved expense line is compared with the
Holded documents and ledger lines it should have produced, each night and on a page: missing
documents, duplicates, orphans and member balances that disagree. Duplicates Holded let through are
listed, verified one by one, and removed from Holded in one action.

**Paying it.** A treasurer ticks creditor accounts, caps each amount, and downloads a bank file that
pays them in one upload; each paid member is emailed that the money is on its way. The file is kept
byte-for-byte as the record of what was sent. A transfer the bank cancelled before it ran is
withdrawn, and the member told.

**Settling it in the books.** When the bank line that paid a transfer appears on the bank feed, the
transfer is booked into Holded against it — by a sweep every two hours, or by a treasurer's click —
paying the member's open invoices oldest first and posting whatever they do not cover as one journal
entry against the bank, then telling Holded the line and the postings are the same money. A bank
line that paid a whole file at once, or lines that carry no remittance text, are only ever booked by
a treasurer. Booking happens at most once per transfer and is resumable when Holded refuses
part-way.

**The member's IBAN.** Finance holds the IBAN a member gives for reimbursement. Expenses reads and
sets it; only an Admin can see it unmasked, one member at a time, and every reveal is audited.

Finance holds personal data — the IBAN, a member's Holded contact link, and their IBAN inside a
payout file — so it answers the member's own request for a copy and honours erasure by deleting the
IBAN and cutting the link. The payout files are the accounting record and survive erasure.

## 2. The shapes

The section's methods, grouped by the question each one answers.

| Shape | The question | Methods | Asked by |
|---|---|---|---|
| **A — Accounts to spend against** | Which accounts exist for spending, and make the missing ones | `GetProvisioningPlanAsync`, `ProvisionAsync`, `GetHoldedAccountIdForCategoryAsync`, `GetCategoryMapAsync`, `CreateOrLinkExpenseAccountAsync`, `SetExpenseAccountActiveAsync`, `ListExpenseAccountsAsync` | Finance's own page; Expenses, to book a line; Workgroups; Backdoor |
| **B — What was spent** | The per-category total over Holded's purchase mirror, what didn't attribute, how the pull went | `GetActualsForYearAsync`, `GetUnmatchedAsync` | Budget's year page; Finance's own pages; Backdoor |
| **C — Whose account is this** | Which Holded creditor account is this member's — set it, correct it, clear it | `GetCreditorContactByUserAsync`, `EnsureCreditorContactAsync`, `SetCreditorAccountNumAsync`, `SetCreditorContactAsync`, `ClearCreditorContactAsync` | Expenses' push path and views; Finance's own page |
| **D — What is owed** | What does the org owe this member, the journal behind it, and does it match the reports | `GetCreditorStatusAsync`, `GetCreditorLedgerAsync`, `ListCreditorAccountsAsync`, `CreditorMatchService` | Expenses; Finance's own pages; Backdoor |
| **E — Paying it** | Is payout configured; make the bank file; what files exist; withdraw a cancelled transfer | `GetSepaPayoutSettings`, `GenerateSepaPayoutAsync`, `GetSepaPayoutsAsync`, `GetSepaTransfersAsync`, `WithdrawSepaTransferAsync` | Finance's own pages; Backdoor |
| **F — Settling it** | Book this transfer, this file, these textless lines, or everything the feed now shows | `BookSepaTransferAsync`, `BookSepaFileAsync`, `BookSepaFileMatchesAsync`, the sweep | Finance's own page; the sweep job |
| **G — Do the books match the reports** | Which expense lines have no document or a duplicate, and which documents nobody filed; remove the duplicates | `GetExpenseReconAsync`, `GetHoldedDuplicatesAsync`, `RemoveHoldedDuplicatesAsync` | Finance's own pages and nightly job; Backdoor |
| **H — Planned money over time** | The active year's plan by week or month, with its VAT, against ticket revenue | `CashFlowService` | Finance's own page |
| **I — The member's IBAN** | What IBAN is on record; set it; show it unmasked | `GetIbanAsync`, `SetIbanAsync`, `RevealIbanAsync` | Expenses; the Admin member page |
| **J — The member's data** | What Finance holds about this member; forget them; follow a merge | `ContributeForUserAsync`, `EraseForUserAsync`, `ReassignAsync` | Gdpr and Users, on the member's behalf |

What the grouping shows:

- **C is the section's real difficulty, and it is essential.** Three writers give deliberately
  different answers to the same collision, because only some of them are guesses. Not collapsible;
  see load-bearing weirdness.
- **D is three resolutions of one derivation.** All three start from the same cached journal lines
  and the same cached Holded contact list, and compute balance and owed the same way. A fourth path
  to either is an inconsistency, not a variation.
- **E and F are D's consumers, never a second balance.** A payout row starts from D's creditor list;
  booking reads the live balance only to cap what a resume may post.
- **F is the section's main write into the books, and the most guarded code in it.** A transfer books
  once, against a line the server re-pairs itself, only while the member's binding still names the
  account and contact the file paid, and only from a treasury account that was configured, never
  inferred.
- **A, C and F each have a half only Finance's own pages and jobs call.** The provisioning plan and
  apply, the manual bind and the unbind, and the sweep live on the internal admin interface beside E and F, not on the public
  cross-section one; their result records live in `Models/`, not the contracts leaf.

## 3. Structure

```
Humans.Finance.Contracts/      the read and write interfaces, the DTOs they return, roles, policies, audit actions
Humans.Finance/
  Section.cs                   DI, admin nav, the three job schedules, policies, the IBAN data migration
  SepaOptions.cs               the organisation's SEPA identity and the treasury accounts
  Controllers/                 the Finance pages and their posts; the Admin-only IBAN reveal
  ViewComponents/              the IBAN card on the Admin member page
  Models/                      view models and the payout file's input records
  Views/Finance/               those pages
  Services/
    Service.cs                 facade for shapes A–G
    HoldedCreditorReader.cs     shape D: status, accounts and statements
    FinancePersonalData.cs      financial export and retention declarations
    SepaBankBooking.cs         shape F and the sweep
    HoldedMatcher.cs           pure attribution, no dependencies
    HoldedDuplicateMatcher.cs  pure duplicate detection, no dependencies
    HoldedDuplicateReview.cs   preview, fresh-sync confirmation and removal
    ExpenseRecon.cs            pure reconciliation report, no dependencies
    SepaPayoutGeneration.cs    preview, plan and persisted payment-file generation
    SepaPaymentFileBuilder.cs  pure pain.001 XML from a batch, no dependencies
    SepaSchema.cs, SepaText.cs the schema and the character rules that builder obeys
    SepaPayoutEmails.cs, FinanceEmails.cs, FinanceEmailPreviews.cs  the payout emails and their preview
    CreditorMatchService.cs    D's reports-vs-ledger match
    CashFlowService.cs         shape H
    PaymentDetailsService.cs   shapes I and J for the IBAN
    I*.cs                      the internal interfaces
  Jobs/                        two Hangfire shims: booking sweep, recon
  Domain/                      the entities and their enums
  Data/                        two repositories over one context, the tables
  Resources/                   the pain.001.001.09 schema the file is validated against
  FinanceResource*.resx        the payout emails' strings, every culture
```

What the shapes say about the inside:

- Shape **D**'s methods share one balance derivation and one accessor for the cached Holded
  contact list in `HoldedCreditorReader`.
- Shape **C**'s collision policy is one predicate with three call-site answers.
- The member creditor block is one range, checked by one predicate wherever an account number is
  judged.
- Shape **E**'s file is pure: the builder takes a batch and returns bytes; the service owns the vendor
  calls, the clock and the rows.
- The active budget year's category names are looked up once, by one helper, for every reader
  (provisioning plan, expense account options, category map, connector overview).
- `Service.cs` composes the internal owners. Creditor reads own their shared contact map and
  projection; payout, duplicate review and export reuse those helpers. Booking, provisioning and
  emails retain their own owners.
- Every job is internal and takes an internal interface: no public type exists only to feed one.
- A view model exists per row shape, not per page.

## 4. Invariants

- Board, `FinanceAdmin` and `Admin` read every `/Finance/*` page this controller serves; every POST
  needs `FinanceAdmin` or `Admin` (`src/Sections/Humans.Finance/Controllers/FinanceController.cs:17`,
  `src/Sections/Humans.Finance/Section.cs:134`, `src/Sections/Humans.Finance/Section.cs:137`).
- Only `Admin` sees a member's IBAN unmasked, and every reveal is audited
  (`src/Sections/Humans.Finance/Controllers/PaymentDetailsAdminController.cs:12`,
  `src/Sections/Humans.Finance/Services/PaymentDetailsService.cs:52`).
- A purchase invoice is attributed as a whole, by its first line's booked account, then by tag, then
  not at all; first match wins (`src/Sections/Humans.Finance/Services/Service.cs:332`,
  `src/Sections/Humans.Finance/Services/HoldedMatcher.cs:56`).
- An invoice counts toward a category's actuals only when Holded has approved it
  (`src/Sections/Humans.Finance/Services/Service.cs:352`).
- An invoice booked to a managed account is matched with no category
  (`src/Sections/Humans.Finance/Services/HoldedMatcher.cs:57`).
- Provisioning and the managed registry are additive in Holded: they create accounts and never
  delete or edit one (`src/Sections/Humans.Finance/Services/Service.cs:156`,
  `src/Sections/Humans.Finance/Services/Service.cs:241`).
- A creditor account, and the Holded contact behind it, belongs to at most one member. Every write
  path checks with one predicate (`src/Sections/Humans.Finance/Services/Service.cs:690`); the manual
  bind and the seed refuse (`src/Sections/Humans.Finance/Services/Service.cs:750`,
  `src/Sections/Humans.Finance/Services/Service.cs:855`), the post-push number write records Holded's
  fact and logs the collision (`src/Sections/Humans.Finance/Services/Service.cs:991`).
- A member's binding is never downgraded from a hand-made link to an automatic one
  (`src/Sections/Humans.Finance/Services/Service.cs:929`).
- A linked Holded contact is never updated from here — only a member with no contact gets one
  created (`src/Sections/Humans.Finance/Services/Service.cs:872`).
- Every read that draws on Holded's contact list for creditor accounts, and the manual bind, are
  filtered to the 40000000–41999999 block (`src/Sections/Humans.Finance/Services/HoldedCreditorReader.cs`,
  `src/Sections/Humans.Finance/Services/Service.cs:743`).
- Balances keep Holded's sign (`Σdebit − Σcredit`) on every DTO; the creditor list and the statement
  flip it for display, and a ledger line's running balance is computed from the member's side
  (`src/Sections/Humans.Finance/Controllers/FinanceController.cs:105`,
  `src/Sections/Humans.Finance/Models/CreditorStatementVm.cs:14`,
  `src/Sections/Humans.Finance/Services/HoldedCreditorReader.cs`).
- A Holded outage costs account names, never a page; anything that is not a vendor failure throws
  (`src/Sections/Humans.Finance/Services/Service.cs:657`).
- A payout transfer pays only a singly-bound account, never more than is owed, never more than the
  posted cap, and only to the IBAN Holded holds for the contact the binding names
  (`src/Sections/Humans.Finance/Services/SepaPayoutGeneration.cs`,
  `src/Sections/Humans.Finance/Services/SepaPayoutGeneration.cs`,
  `src/Sections/Humans.Finance/Services/SepaPayoutGeneration.cs`,
  `src/Sections/Humans.Finance/Services/SepaPaymentFileBuilder.cs:174`).
- An account with an unbooked transfer in an earlier file, or with duplicate documents in Holded,
  or whose documents cannot be read, is not paid again
  (`src/Sections/Humans.Finance/Services/SepaPayoutGeneration.cs`,
  `src/Sections/Humans.Finance/Services/SepaPayoutGeneration.cs`).
- A generated file validates against the schema before it is stored, and is stored as sent
  (`src/Sections/Humans.Finance/Services/SepaPaymentFileBuilder.cs:123`,
  `src/Sections/Humans.Finance/Services/Service.cs:1160`).
- A transfer is withdrawn only while nothing has moved: never once booked, tied to a bank line, or
  carrying a Holded payment (`src/Sections/Humans.Finance/Services/Service.cs:1215`).
- A transfer books at most once: the row's `BookedAt` refuses a second booking, and every booking on
  the server runs one at a time (`src/Sections/Humans.Finance/Services/SepaBankBooking.cs:621`,
  `src/Sections/Humans.Finance/Services/SepaBankBooking.cs:493`).
- Booking re-pairs the bank line itself and refuses a binding that no longer names the transfer's
  account or contact (`src/Sections/Humans.Finance/Services/SepaBankBooking.cs:687`,
  `src/Sections/Humans.Finance/Services/SepaBankBooking.cs:639`,
  `src/Sections/Humans.Finance/Services/SepaBankBooking.cs:654`).
- Booking writes two kinds of entry into the books and nothing else: a payment against an approved
  purchase document, and one journal entry for the remainder against the configured treasury ledger
  account (`src/Sections/Humans.Finance/Services/SepaBankBooking.cs:770`,
  `src/Sections/Humans.Finance/Services/SepaBankBooking.cs:787`).
- A booking is saved before the bank line is reconciled
  (`src/Sections/Humans.Finance/Services/SepaBankBooking.cs:813`).
- The sweep books a transfer only against a line whose text names the account and that matches
  exactly one transfer; whole-file and textless lines are booked only by a person
  (`src/Sections/Humans.Finance/Services/SepaBankBooking.cs:343`,
  `src/Sections/Humans.Finance/Services/SepaBankBooking.cs:346`).
- A duplicate document is deleted only when its notes match the push exactly; a contact is deleted
  only once every document it held was deleted without a failure
  (`src/Sections/Humans.Finance/Services/HoldedDuplicateReview.cs`,
  `src/Sections/Humans.Finance/Services/HoldedDuplicateReview.cs`).
- A raw IBAN lives in the payment-details row, the bank file, the transfer row and the contact
  create that hands it to Holded; audit entries, exports and cross-section rows carry it masked
  (`src/Sections/Humans.Finance/Services/Service.cs:1117`,
  `src/Sections/Humans.Finance/Services/PaymentDetailsService.cs:64`).
- Erasure deletes the member's IBAN row and contact link and nothing else
  (`src/Sections/Humans.Finance/Services/Service.cs:1651`,
  `src/Sections/Humans.Finance/Services/PaymentDetailsService.cs:74`).

## 5. Seams

Specified, not built. Not ranked, not struck.

- **Line-level attribution.** A multi-line invoice booked across several Holded accounts lands wholly
  on the first line's category. Deliberate v1 simplification.
- **Link-check sync** (peterdrier/Humans#1777). A name or IBAN change after a member's first push does
  not reach Holded, and nothing verifies a binding still matches its contact.
- **Duplicate removal as a background job** (peterdrier/Humans#2210). Apply runs inside the request.
- **Settling a transfer Holded holds a payment for but Finance never booked**
  (peterdrier/Humans#2021). Refused today.
- **`holded_*` tables under a section called Finance** (nobodies-collective/Humans#1012). A rename is
  schema work, deferred wholesale.

## 6. Deliberately not done

- **No unique DB index on `SupplierAccountNum`.** The automatic writers run inside the outbox drain,
  where a constraint violation strands a created Holded document as permanently-failed, and the index
  would have to be created against production rows that may already collide. Enforcement lives in the
  service ([`db-enforcement-minimal`](../../../../memory/architecture/db-enforcement-minimal.md)).
- **No concurrency token on the binding row**
  ([`no-concurrency-tokens`](../../../../memory/architecture/no-concurrency-tokens.md)); the mitigation
  is the re-read immediately before each write.
- **No caching decorator over the service.** The one repeated vendor call is cached at its own call
  site with a 2-minute TTL.
- **No localized admin pages.** The `.resx` set carries the payout emails only; the pages are
  admin-only ([`localization-admin-exempt`](../../../../memory/code/localization-admin-exempt.md)).
- **No per-report paid state.** A SEPA payout pays a balance, not a report.
- **No nullable `Balance` on the creditor DTOs.** Both reads return null wholesale when an account has
  no cached lines. `CreditorAccountRow` keeps its `decimal?` — the admin list carries accounts
  with no lines yet.
- **No split of `Service.cs`'s interfaces per shape.** The shapes share the repository, the clock
  and the contact cache; the self-contained flows are already their own classes behind it.
- **No SEPA erasure.** A payout file is the accounting record; the member's IBAN inside it outlives
  the member's contact link.
- **No stored payment references beyond the run's own.** The bank line id is the record; a resume
  sums what was already posted under the transfer's own tag.

## Load-bearing weirdness

Settled; do not re-litigate.

- **Three write paths, three different collision remedies.** Manual bind refuses. The post-push
  number write records Holded's fact and logs the collision. Seed adoption refuses. The split is by
  whether the value is our guess or Holded's statement.
- **Balance sign flips only for display.** Holded's `Σdebit − Σcredit` travels on every balance; the
  two admin views invert it, and the statement's running balance is accumulated from the member's
  side so it reads the same way as the flipped total.
- **Actuals come from the invoice mirror, not the ledger.** The budget pages are IVA-inclusive and
  the ledger account is net, and the ledger carries drafts Holded has not approved.
- **Full-pull every sync.** Holded's purchase endpoint has no usable incremental key.
- **Never index a nested Holded JSON node.** Holded serialises an absent sub-record as an empty
  array; one such contact once blanked every account name in production.
- **The unresolved card is a feature of a missing retry.** It is the only place members whose account
  number never resolved are bindable.
- **The treasury accounts are never inferred.** With `Sepa:TreasuryAccountId` or
  `Sepa:TreasuryLedgerAccount` unset, no booking is offered or made.
- **Booking and duplicate removal take no cancellation token.** A half-posted payment, or a
  half-deleted duplicate set, is worse than a slow one.
- **One process-wide gate serialises bookings.** One server; the gate is cheaper than a lock row.
- **`Sepa:Creditor*` names the debtor.** The config keys predate the payout feature; in a payout the
  organisation is the debtor. Renaming the keys is a deploy change for a spelling.
- **Cash flow lives here but reads only Budget and Tickets.** Peter folded cash flow into Finance;
  it owns no table.

## History

| Run | Anchor | Headline | PR |
|---|---|---|---|
| [2026-11-02](../../../../docs/health/runs/2026-11-02-Finance.md) | `3b63d6603` | Fourth target; the section moved and the old target was wrong on Board's access. A public contract interface existed only to feed the sweep job and leaves; nothing pinned the write policy on each POST, now a test does; one category lookup and one creditor-block check instead of several. No code defect found. | [#2240](https://github.com/peterdrier/Humans/pull/2240) |
| [2026-10-02](../../../../docs/health/runs/2026-10-02-Finance.md) | `119ba6503` | Third target. A code comment named a member with their creditor balance; Finance.md denied the remainder journal entry and its own resx and still called the #995 race open; the Finance-only provisioning and bind methods left the public contract. No code defect found; bind, unbind and provisioning wrote no audit entry (FIN-4, closed in review). | [#1883](https://github.com/peterdrier/Humans/pull/1883) |
| [2026-09-06](../../../../docs/health/runs/2026-09-06-Finance.md) | `10199a23` | Second target; the section moved (SEPA payout and GDPR shapes, read split shipped). Docs and comments narrated the moves and counted things; named invariants had no test and one test pinned an absence. No code defect found. A raw IBAN on the creditor statement contradicts the docs — Peter's call. | [#1613](https://github.com/peterdrier/Humans/pull/1613) |
| [2026-08-18](../../../../docs/health/runs/2026-08-18-Finance.md) | `41fd7374d` | First target. Doc led with 23 routes the section does not serve; a tag-collision bug in provisioning; a published DTO with no consumer. Prod code −113 lines, tests 55 → 92, mutation 34.4% → 57.9%. | [#1374](https://github.com/peterdrier/Humans/pull/1374) |
