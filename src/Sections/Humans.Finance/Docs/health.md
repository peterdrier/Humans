# Finance — health

**Assessment target.** Derived from the section's behaviour, not from any scan. Regenerated every
section-doctor run and diffed against the previous run's copy.

- Last assessed: 2026-09-06
- Anchor: 10199a23
- Previous target: 2026-08-18. Diff: the section moved. Shapes the previous target did not
  have — paying what is owed (SEPA generate and book) and the member's own data (GDPR export and
  erasure) — and the read/write split the previous target named as a seam has shipped. Structure
  and the Holded write-boundary statement updated to match; the rest holds.

## 1. What the section does

Finance is the treasurer's window onto what the organisation actually spent and actually owes its
members, with Holded — the outside bookkeeping system — as the system of record.

What happens here:

**Money out of the org, by budget category.** Every purchase invoice the bookkeeper enters in
Holded is pulled in nightly and attributed to one budget category, so the budget pages can show
what a category really spent against what it planned. Attribution is by the account the bookkeeper
booked the invoice to, falling back to a tag; anything that attributes to nothing lands in a
worklist a treasurer works through by hand. Only invoices the bookkeeper has approved count — a
draft is not yet a real cost.

**Getting the accounts to attribute to.** Each budget category needs its own Holded expense account
before an invoice can be booked to it. A treasurer sees which categories have one, which need one,
and which have one for a category that no longer exists, and creates the missing ones in Holded in
one action. Never removes anything.

**Money the org owes members.** A member who submits an expense report becomes a creditor of the
organisation, with a numbered account in Holded's books. Finance keeps the link between the member
and that account, shows every such account with what is owed on it, and shows the statement behind
any one of them. The link is created automatically the first time a member's report is pushed, and
a treasurer can correct it by hand when the automatic attempt guesses wrong or does not resolve at
all.

**Paying it.** A treasurer ticks creditor accounts, caps each amount, and downloads a bank file that
pays them in one upload. After the bank has taken the file, each transfer is booked into Holded as a
payment against that member's open purchase documents, oldest first, so the next ledger pull shows
the balance settled. The file is kept byte-for-byte as the record of what was sent; booking is the
one thing that moves a transfer on, and it happens at most once.

The organisation's books are Holded's. Finance reads them, creates expense accounts and member
contacts, and posts exactly one kind of entry: a payment against a purchase document, for a transfer
that already left the bank. It never posts a journal entry of its own.

Finance also holds personal data — a member's Holded contact link, and their IBAN inside a payout
file — so it answers the member's own request for a copy and honours erasure by cutting the link.
The payout files themselves are the accounting record and survive erasure.

## 2. The shapes

The section's methods, grouped by the question each one answers. Cross-section callers hold
`IHoldedFinanceServiceRead` (reads) or `IHoldedFinanceService` (its writes); the admin interface is
Finance's own page's.

| Shape | The question | Methods | Asked by |
|---|---|---|---|
| **A — Accounts to spend against** | Which categories have a Holded account, and make the missing ones | `GetProvisioningPlanAsync`, `ProvisionAsync`, `GetHoldedAccountIdForCategoryAsync` | Finance's own page; Expenses, to book a line |
| **B — What was spent** | Pull the invoices; the per-category total, what didn't attribute, and how the pull went | `SyncAsync`, `GetActualsForYearAsync`, `GetUnmatchedAsync`, `GetDocSyncInfoAsync`, `GetConnectorOverviewAsync` | Nightly job; Budget's year page; Finance's own pages; the Holded admin screen |
| **C — Whose account is this** | Which Holded creditor account is this member's — set it, correct it, clear it | `GetCreditorContactByUserAsync`, `EnsureCreditorContactAsync`, `SetCreditorAccountNumAsync`, `SetCreditorContactAsync`, `ClearCreditorContactAsync` | Expenses' push path; Finance's own page |
| **D — What is owed** | What does the org owe this member, and what is the journal behind it | `GetCreditorStatusAsync`, `GetCreditorLedgerAsync`, `ListCreditorAccountsAsync` | Expenses' member and admin views; Finance's own page |
| **E — Paying it** | Make the bank file for these accounts; what files exist; record that one transfer was paid | `GenerateSepaPayoutAsync`, `GetSepaPayoutsAsync`, `BookSepaTransferAsync` | Finance's own pages only |
| **F — The member's data** | What Finance holds about this member; forget them | `ContributeForUserAsync`, `EraseForUserAsync` | Gdpr, on the member's behalf |

What the grouping shows:

- **C is the section's real difficulty, and it is essential.** Three writers give deliberately
  different answers to the same collision, because only some of them are guesses. Not collapsible;
  see load-bearing weirdness.
- **D is three resolutions of one derivation.** All three start from the same cached journal lines
  and the same cached Holded contact list, and compute balance and owed the same way. The shapes
  differ because the callers differ; the arithmetic and the contact lookup must exist once.
- **E is D's consumer, not a fifth resolution of it.** A payout row starts from the same creditor
  list D serves, so the amount, the name and the IBAN come from D's lookup — E adds the cap, the
  file and the booking, never a second balance.
- **E's booking is the section's only write into the books.** It is guarded harder than any other
  write here: a transfer books once, only after a file exists, only against documents Holded still
  shows open, and only from a treasury account that was named, never inferred.
- **B's `GetDocSyncInfoAsync` carries a C fact.** Its result includes a count of creditor bindings,
  which is not sync state. One screen's convenience shaped a contract type.
- **The read/write split is done.** Budget and Expenses' views hold the read interface; only
  Expenses' push path and the Holded sync hold the writes. The previous target's Seam 1 is closed.

## 3. Structure

The layout the shapes imply:

```
Humans.Finance.Contracts/      the read and read+write interfaces, the DTOs their methods return
Humans.Finance/
  Section.cs                   DI
  Controllers/                 one controller — the pages and their posts
  Models/                      view models for those pages
  Views/Finance/               those pages
  Services/
    Service.cs                 the shapes
    HoldedMatcher.cs           pure attribution, no dependencies
    SepaPaymentFileBuilder.cs  pure pain.001 XML from a batch, no dependencies
    SepaSchema.cs, SepaText.cs the schema and the character rules that builder obeys
    IHoldedFinanceAdminService.cs  Finance's own page's interface (E, plus the connector overview)
  Domain/                      the entities and their enums
  Data/                        one repository over one context, the tables
  Resources/                   the pain.001.001.09 schema the file is validated against
```

That is what is there. The file structure is right; the work is inside the files, not between them.

What the shapes say about the inside:

- Shape **D**'s three methods share one derivation of balance and owed from a set of cached journal
  lines, and one accessor for the cached Holded contact list. A fourth path to either is an
  inconsistency, not a variation.
- Shape **C**'s collision policy is one rule with three call-site answers. The rule lives in one
  predicate; each writer states its own answer beside its own write. Not three rules.
- Shape **E**'s file is pure: the builder takes a batch and returns bytes, and the service around it
  owns the vendor call, the clock and the row. Everything that can be unit-tested without Holded is
  in the builder.
- A view model exists per row shape, not per page. Two page sections showing the same row of facts
  share one type.

## 4. Invariants

- Only `FinanceAdmin` or `Admin` reaches any `/Finance/*` route.
- A purchase invoice is attributed as a whole, by its first line's booked account, then by tag, then
  not at all. First match wins.
- An invoice counts toward a category's actuals only when Holded has approved it.
- Provisioning is additive: it creates Holded accounts and map rows, never deletes or edits.
- A creditor account, and the Holded contact behind it, belongs to at most one member. Every write
  path checks; they differ only in the remedy, and the remedy follows from whether the value is our
  guess (refuse) or Holded's own statement of fact (write it, and make the collision visible).
- A member's binding is never silently downgraded from a hand-made link to an automatic one.
- Every read that draws on Holded's contact list is filtered to the `40000000`–`41999999` block.
  Outside that block a supplier number belongs to an ordinary vendor, not a member.
- Balance keeps Holded's sign everywhere except the two admin views, which flip it once for display
  so a positive figure is money owed to the member.
- A Holded outage costs account *names*, never a page. Anything that is not a vendor failure throws.
- Finance reads Holded's journal; it never writes one. Its single write into the books is a payment
  against a purchase document, and only for a transfer in a generated file.
- A payout transfer pays a member's own account only, never more than is owed, never more than the
  cap the treasurer posted, and only to an IBAN Holded holds for that contact.
- A generated file is immutable and is the record: the XML is stored as sent and validates against
  the schema before it is stored.
- A transfer books at most once. A second attempt is a no-op that says so, not a second payment.
- A raw IBAN lives in the bank file, in the transfer row behind it, and in the contact write that
  hands it to Holded. Every log line, audit entry and screen shows it masked.
- Erasure removes the member's contact link and nothing else. The Holded contact is Holded's
  record; the payout files and transfers are the accounting record, and both stay.
- Finance touches no Budget, Holded or Tickets table — only their contracts.

## 5. Seams

Specified, not built. Not ranked, not struck; items touching these callers are shaped by them.

- **Line-level attribution.** A multi-line invoice booked across several Holded accounts lands wholly
  on the first line's category. Deliberate v1 simplification, still true.
- **No retry on creditor-number resolution** (nobodies-collective/Humans#972). The one-shot lookup
  misses and nothing tries again; the unresolved card exists because of it.
- **Binding writes can still lose a concurrent unbind** (nobodies-collective/Humans#995) outside the
  steady state. The re-read before write shrinks the window; closing it needs an update-only
  repository write.
- **`holded_*` tables under a section called Finance** (nobodies-collective/Humans#1012). A rename is
  schema work, deferred wholesale.
- **The creditor statement shows the IBAN raw** (`Views/Finance/CreditorStatement.cshtml`). The
  invariant in §4 says every screen masks it; whether the view masks or the invariant narrows is
  open (run 2026-09-06, finding 1).
- **Booking is not cancellable mid-flight.** `BookSepaTransferAsync` takes no cancellation token by
  design — a half-posted payment is worse than a slow one — so the whole posting loop runs to the
  end once started.

## 6. Deliberately not done

- **No unique DB index on `SupplierAccountNum`.** The automatic writers run unattended inside outbox
  drain, where a constraint violation strands a created Holded document as permanently-failed, and the
  index would have to be created against production rows that may already collide. Enforcement lives
  in the service ([`db-enforcement-minimal`](../../../../memory/architecture/db-enforcement-minimal.md)).
- **No concurrency token on the binding row**
  ([`no-concurrency-tokens`](../../../../memory/architecture/no-concurrency-tokens.md)); the mitigation
  is the re-read immediately before each write.
- **No caching decorator over the service.** The one repeated vendor call is cached at its own call
  site with a 2-minute TTL, which is the whole caching need.
- **No `.resx`.** English-only finance-admin pages with zero localizer call sites.
- **No per-report paid state.** Payment is an account-level fact; attributing it to one report fakes
  an attribution that does not exist. A SEPA payout pays a balance, not a report.
- **No nullable `Balance` on the creditor DTOs.** Both reads return null wholesale when an account has
  no cached lines, so a returned status or ledger always has a balance. `HoldedCreditorAccountRow`
  keeps its `decimal?` — the admin list does carry accounts with no lines yet.
- **No `Name` on `HoldedCreditorLedger`.** It was exactly `Contact?.Name`; the statement header reads
  through the contact (Peter, 2026-08-18).
- **No split of `Service.cs` into per-shape services.** The shapes share the repository, the clock
  and the contact cache; splitting would trade one long file for several files and one coordinating
  them. The pure parts (matcher, file builder) are already out.
- **No bank-side reconciliation.** Nothing reads a bank statement back; "the file was uploaded" is a
  human fact the treasurer asserts by pressing Book.
- **No SEPA erasure.** A payout file is the accounting record; the member's IBAN inside it outlives
  the member's contact link.

## Load-bearing weirdness

Settled; do not re-litigate.

- **Three write paths, three different collision remedies.** Manual bind refuses. The post-push
  number write records Holded's fact and logs the collision. Seed adoption refuses. The split is by
  whether the value is our guess or Holded's statement — refusing everywhere would strand real
  payables, writing everywhere would silently merge two members' money.
- **Balance sign flips only in the view.** Holded's `Σdebit − Σcredit` travels everywhere; exactly
  two views invert it, because a treasurer reads "owed to the member" as positive.
- **Actuals come from the invoice mirror, not the ledger.** The budget pages are IVA-inclusive and
  the ledger account is net, and the ledger carries drafts Holded has not approved.
- **Full-pull every sync.** Holded's purchase endpoint has no usable incremental key — its date
  filters read a field that is null on real invoices.
- **Never index a nested Holded JSON node.** Holded serialises an absent sub-record as an empty
  array; one such contact once blanked every account name in production.
- **The unresolved card is a feature of a missing retry.** It exists because nothing retries the
  number resolution, and it is the only place those members are bindable.
- **The treasury account is never inferred.** With `Sepa:TreasuryAccountId` unset the Book buttons do
  not render; Holded would otherwise pay from whatever it defaults to, and that money is real.
- **`Sepa:Creditor*` names the debtor.** The config keys predate the payout feature; in a payout the
  organisation is the debtor, and that is where the values land. Renaming the keys is a deploy
  change for a spelling.

## History

| Run | Anchor | Headline | PR |
|---|---|---|---|
| [2026-09-06](../../../../docs/health/runs/2026-09-06-Finance.md) | `10199a23` | Second target; the section moved (SEPA payout and GDPR shapes, read split shipped). Docs and comments narrated the moves and counted things; named invariants had no test and one test pinned an absence. No code defect found. A raw IBAN on the creditor statement contradicts the docs — Peter's call. | [#1613](https://github.com/peterdrier/Humans/pull/1613) |
| [2026-08-18](../../../../docs/health/runs/2026-08-18-Finance.md) | `41fd7374d` | First target. Doc led with 23 routes the section does not serve; a tag-collision bug in provisioning; a published DTO with no consumer. Prod code −113 lines, tests 55 → 92, mutation 34.4% → 57.9%. | [#1374](https://github.com/peterdrier/Humans/pull/1374) |
