# Finance — health

**Assessment target.** Derived from the section's behaviour, not from any scan. Regenerated every
section-doctor run and diffed against the previous run's copy.

- Last assessed: 2026-10-02
- Anchor: 119ba6503
- Previous target: 2026-09-06. Diff: the section moved. Booking is now driven by the bank line —
  an unattended sweep and a whole-file Process join the Book button — and it settles the balance
  with a journal entry for whatever the documents do not cover, which the previous target's "never
  posts a journal entry of its own" no longer survives. New since then: expense accounts outside the
  budget map (Workgroups' managed registry), the member's payout email, and Backdoor's read-only
  views. The previous target was also wrong in one place: it said Finance has no `.resx`.

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

**Money the org owes members.** A member who submits an expense report becomes a creditor of the
organisation, with a numbered account in Holded's books. Finance keeps the link between the member
and that account, shows every such account with what is owed on it, and the statement behind any
one of them. The link is made automatically the first time a member's report is pushed; a treasurer
corrects it by hand when that guess is wrong or never resolved.

**Paying it.** A treasurer ticks creditor accounts, caps each amount, and downloads a bank file that
pays them in one upload; each paid member is emailed that the money is on its way. The file is kept
byte-for-byte as the record of what was sent.

**Settling it in the books.** When the bank line that paid a transfer appears on the bank feed, the
transfer is booked into Holded against it — by a sweep every two hours, or by a treasurer's click —
paying the member's open invoices oldest first and posting whatever they do not cover as one journal
entry against the bank, then telling Holded the line and the postings are the same money. A bank
line that paid a whole file at once is only ever booked by a treasurer. Booking happens at most once
per transfer and is resumable when Holded refuses part-way.

Finance also holds personal data — a member's Holded contact link, and their IBAN inside a payout
file — so it answers the member's own request for a copy and honours erasure by cutting the link.
The payout files are the accounting record and survive erasure.

## 2. The shapes

The section's methods, grouped by the question each one answers.

| Shape | The question | Methods | Asked by |
|---|---|---|---|
| **A — Accounts to spend against** | Which accounts exist for spending, and make the missing ones | `GetProvisioningPlanAsync`, `ProvisionAsync`, `GetHoldedAccountIdForCategoryAsync`, `GetCategoryMapAsync`, `CreateOrLinkExpenseAccountAsync`, `SetExpenseAccountActiveAsync`, `ListExpenseAccountsAsync` | Finance's own page; Expenses, to book a line; Workgroups; Backdoor |
| **B — What was spent** | Pull the invoices; the per-category total, what didn't attribute, how the pull went | `SyncAsync`, `GetActualsForYearAsync`, `GetUnmatchedAsync`, `GetDocSyncInfoAsync`, `GetConnectorOverviewAsync` | Holded's nightly job and page; Budget's year page; Finance's own pages; Backdoor |
| **C — Whose account is this** | Which Holded creditor account is this member's — set it, correct it, clear it | `GetCreditorContactByUserAsync`, `EnsureCreditorContactAsync`, `SetCreditorAccountNumAsync`, `SetCreditorContactAsync`, `ClearCreditorContactAsync` | Expenses' push path and views; Finance's own page |
| **D — What is owed** | What does the org owe this member, and the journal behind it | `GetCreditorStatusAsync`, `GetCreditorLedgerAsync`, `ListCreditorAccountsAsync` | Expenses; Finance's own pages; Backdoor |
| **E — Paying it** | Is payout configured; make the bank file; what files exist | `GetSepaPayoutSettings`, `GenerateSepaPayoutAsync`, `GetSepaPayoutsAsync`, `GetSepaTransfersAsync` | Finance's own pages; Backdoor |
| **F — Settling it** | Book this transfer, this file, or everything the feed now shows | `BookSepaTransferAsync`, `BookSepaFileAsync`, `RunAsync` | Finance's own page; the sweep job |
| **G — The member's data** | What Finance holds about this member; forget them | `ContributeForUserAsync`, `EraseForUserAsync` | Gdpr, on the member's behalf |

What the grouping shows:

- **C is the section's real difficulty, and it is essential.** Three writers give deliberately
  different answers to the same collision, because only some of them are guesses. Not collapsible;
  see load-bearing weirdness.
- **D is three resolutions of one derivation.** All three start from the same cached journal lines
  and the same cached Holded contact list, and compute balance and owed the same way. A fourth path
  to either is an inconsistency, not a variation.
- **E and F are D's consumers, never a second balance.** A payout row starts from D's creditor list;
  booking reads the live balance only to cap what a resume may post.
- **F is the section's only write into the books, and the most guarded code in it.** A transfer books
  once, against a line the server re-pairs itself, only while the member's binding still names the
  account and contact the file paid, and only from a treasury account that was configured, never
  inferred.
- **A and C each have a half only Finance's own pages call.** The provisioning plan and apply, the
  manual bind and the unbind live on the internal admin interface beside E and F, not on the public
  cross-section one; their result records live in `Models/`, not the contracts leaf.
- **B's `GetDocSyncInfoAsync` carries a C fact.** Its result includes a count of creditor bindings,
  which is not sync state. Holded's page reads it.

## 3. Structure

```
Humans.Finance.Contracts/      the read and read+write interfaces, the sweep's seam, the DTOs they return
Humans.Finance/
  Section.cs, Section*.cs      DI, admin nav, the sweep's schedule, policies
  SepaOptions.cs               the organisation's SEPA identity and the treasury accounts
  Controllers/                 one controller — the pages and their posts
  Models/                      view models and the payout file's input records
  Views/Finance/               those pages
  Services/
    Service.cs                 the shapes
    HoldedMatcher.cs           pure attribution, no dependencies
    SepaPaymentFileBuilder.cs  pure pain.001 XML from a batch, no dependencies
    SepaSchema.cs, SepaText.cs the schema and the character rules that builder obeys
    IHoldedFinanceAdminService.cs  Finance's own pages' interface
    FinanceEmails.cs, FinanceEmailPreviews.cs  the payout email and its preview sample
  Jobs/                        the sweep's Hangfire shim
  Domain/                      the entities and their enums
  Data/                        one repository over one context, the tables
  Resources/                   the pain.001.001.09 schema the file is validated against
  FinanceResource*.resx        the payout email's strings, six cultures
```

What the shapes say about the inside:

- Shape **D**'s three methods share one balance derivation and one accessor for the cached Holded
  contact list.
- Shape **C**'s collision policy is one predicate with three call-site answers.
- Shape **E**'s file is pure: the builder takes a batch and returns bytes; the service owns the vendor
  calls, the clock and the rows.
- The active budget year's category names are looked up for four readers (provisioning plan, expense
  account options, category map, connector overview). One lookup, not four.
- A view model exists per row shape, not per page.

## 4. Invariants

- Only `FinanceAdmin` or `Admin` reaches any `/Finance/*` route this section serves
  (`src/Sections/Humans.Finance/Controllers/FinanceController.cs:19`).
- A purchase invoice is attributed as a whole, by its first line's booked account, then by tag, then
  not at all; first match wins (`src/Sections/Humans.Finance/Services/Service.cs:386`,
  `src/Sections/Humans.Finance/Services/HoldedMatcher.cs:56`).
- An invoice counts toward a category's actuals only when Holded has approved it
  (`src/Sections/Humans.Finance/Services/Service.cs:432`).
- An invoice booked to a managed account is matched with no category
  (`src/Sections/Humans.Finance/Services/Service.cs:414`).
- Provisioning and the managed registry are additive in Holded: they create accounts and never
  delete or edit one (`src/Sections/Humans.Finance/Services/Service.cs:162`,
  `src/Sections/Humans.Finance/Services/Service.cs:244`).
- A creditor account, and the Holded contact behind it, belongs to at most one member. Every write
  path checks with one predicate (`src/Sections/Humans.Finance/Services/Service.cs:774`); the manual
  bind and the seed refuse (`src/Sections/Humans.Finance/Services/Service.cs:838`,
  `src/Sections/Humans.Finance/Services/Service.cs:919`), the post-push number write records Holded's
  fact and logs the collision (`src/Sections/Humans.Finance/Services/Service.cs:1005`).
- A member's binding is never downgraded from a hand-made link to an automatic one
  (`src/Sections/Humans.Finance/Services/Service.cs:990`).
- A linked Holded contact is never updated from here — only a member with no contact gets one
  created (`src/Sections/Humans.Finance/Services/Service.cs:938`).
- Every read that draws on Holded's contact list for creditor accounts, and the manual bind, are
  filtered to the `CreditorAccountMin`–`CreditorAccountMax` block (40000000–41999999) (`src/Sections/Humans.Finance/Services/Service.cs:632`,
  `src/Sections/Humans.Finance/Services/Service.cs:830`).
- Balance keeps Holded's sign everywhere except the two admin views, which flip it once for display
  (`src/Sections/Humans.Finance/Controllers/FinanceController.cs:80`,
  `src/Sections/Humans.Finance/Models/CreditorStatementVm.cs:12`).
- A Holded outage costs account names, never a page; anything that is not a vendor failure throws
  (`src/Sections/Humans.Finance/Services/Service.cs:735`).
- A payout transfer pays only a singly-bound account, never more than is owed, never more than the
  posted cap, and only to the IBAN Holded holds for the contact the binding names
  (`src/Sections/Humans.Finance/Services/Service.cs:1091`,
  `src/Sections/Humans.Finance/Services/Service.cs:1097`,
  `src/Sections/Humans.Finance/Services/Service.cs:1104`,
  `src/Sections/Humans.Finance/Services/SepaPaymentFileBuilder.cs:158`).
- A generated file validates against the schema before it is stored, and is stored as sent
  (`src/Sections/Humans.Finance/Services/SepaPaymentFileBuilder.cs:107`,
  `src/Sections/Humans.Finance/Services/Service.cs:1159`).
- A transfer books at most once: the row's `BookedAt` refuses a second booking, and every booking on
  the server runs one at a time (`src/Sections/Humans.Finance/Services/Service.cs:1674`,
  `src/Sections/Humans.Finance/Services/Service.cs:1529`).
- Booking re-pairs the bank line itself and refuses a binding that no longer names the transfer's
  account or contact (`src/Sections/Humans.Finance/Services/Service.cs:1687`,
  `src/Sections/Humans.Finance/Services/Service.cs:1698`,
  `src/Sections/Humans.Finance/Services/Service.cs:1732`).
- Booking writes two kinds of entry into the books and nothing else: a payment against an approved
  purchase document, and one journal entry for the remainder against the configured treasury ledger
  account (`src/Sections/Humans.Finance/Services/Service.cs:1828`,
  `src/Sections/Humans.Finance/Services/Service.cs:1846`).
- A booking is saved before the bank line is reconciled
  (`src/Sections/Humans.Finance/Services/Service.cs:1883`).
- The sweep books a transfer only against a line that names exactly one transfer; a whole-file line
  is booked only by a person (`src/Sections/Humans.Finance/Services/Service.cs:1503`).
- A raw IBAN lives in the bank file, the transfer row and the contact create that hands it to Holded;
  audit entries and cross-section rows carry it masked
  (`src/Sections/Humans.Finance/Services/Service.cs:1118`,
  `src/Sections/Humans.Finance/Services/Service.cs:702`).
- Erasure removes the member's contact link and nothing else
  (`src/Sections/Humans.Finance/Services/Service.cs:2337`).

## 5. Seams

Specified, not built. Not ranked, not struck.

- **Line-level attribution.** A multi-line invoice booked across several Holded accounts lands wholly
  on the first line's category. Deliberate v1 simplification.
- **Link-check sync** (peterdrier/Humans#1777). A name or IBAN change after a member's first push does
  not reach Holded, and nothing verifies a binding still matches its contact.
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
- **No localized admin pages.** The `.resx` set carries the payout email only; the pages are
  admin-only ([`localization-admin-exempt`](../../../../memory/code/localization-admin-exempt.md)).
- **No per-report paid state.** A SEPA payout pays a balance, not a report.
- **No nullable `Balance` on the creditor DTOs.** Both reads return null wholesale when an account has
  no cached lines. `HoldedCreditorAccountRow` keeps its `decimal?` — the admin list carries accounts
  with no lines yet.
- **No split of `Service.cs` into per-shape services.** The shapes share the repository, the clock
  and the contact cache; the pure parts (matcher, file builder) are already out.
- **No SEPA erasure.** A payout file is the accounting record; the member's IBAN inside it outlives
  the member's contact link.
- **No stored payment references.** The bank line id is the record; a resume sums what was already
  posted under the transfer's own tag instead.

## Load-bearing weirdness

Settled; do not re-litigate.

- **Three write paths, three different collision remedies.** Manual bind refuses. The post-push
  number write records Holded's fact and logs the collision. Seed adoption refuses. The split is by
  whether the value is our guess or Holded's statement.
- **Balance sign flips only in the view.** Holded's `Σdebit − Σcredit` travels everywhere; exactly
  two views invert it.
- **Actuals come from the invoice mirror, not the ledger.** The budget pages are IVA-inclusive and
  the ledger account is net, and the ledger carries drafts Holded has not approved.
- **Full-pull every sync.** Holded's purchase endpoint has no usable incremental key.
- **Never index a nested Holded JSON node.** Holded serialises an absent sub-record as an empty
  array; one such contact once blanked every account name in production.
- **The unresolved card is a feature of a missing retry.** It is the only place members whose account
  number never resolved are bindable.
- **The treasury accounts are never inferred.** With `Sepa:TreasuryAccountId` or
  `Sepa:TreasuryLedgerAccount` unset, no booking is offered or made.
- **Booking takes no cancellation token.** A half-posted payment is worse than a slow one.
- **One process-wide gate serialises bookings.** One server; the gate is cheaper than a lock row.
- **`Sepa:Creditor*` names the debtor.** The config keys predate the payout feature; in a payout the
  organisation is the debtor. Renaming the keys is a deploy change for a spelling.

## History

| Run | Anchor | Headline | PR |
|---|---|---|---|
| [2026-10-02](../../../../docs/health/runs/2026-10-02-Finance.md) | `119ba6503` | Third target. A code comment named a member with their creditor balance; Finance.md denied the remainder journal entry and its own resx and still called the #995 race open; the Finance-only provisioning and bind methods left the public contract. No code defect found; bind, unbind and provisioning write no audit entry (FIN-4). | [#1883](https://github.com/peterdrier/Humans/pull/1883) |
| [2026-09-06](../../../../docs/health/runs/2026-09-06-Finance.md) | `10199a23` | Second target; the section moved (SEPA payout and GDPR shapes, read split shipped). Docs and comments narrated the moves and counted things; named invariants had no test and one test pinned an absence. No code defect found. A raw IBAN on the creditor statement contradicts the docs — Peter's call. | [#1613](https://github.com/peterdrier/Humans/pull/1613) |
| [2026-08-18](../../../../docs/health/runs/2026-08-18-Finance.md) | `41fd7374d` | First target. Doc led with 23 routes the section does not serve; a tag-collision bug in provisioning; a published DTO with no consumer. Prod code −113 lines, tests 55 → 92, mutation 34.4% → 57.9%. | [#1374](https://github.com/peterdrier/Humans/pull/1374) |
