# Finance — health

**Assessment target.** Derived from the section's behaviour, not from any scan. Regenerated every
section-doctor run and diffed against the previous run's copy.

- Last assessed: 2026-08-18
- Anchor: 41fd7374d
- Previous target: none — this is Finance's first `health.md`, so there is nothing to diff against.

## 1. What the section does

Finance is the treasurer's window onto what the organisation actually spent and actually owes its
members, with Holded — the outside bookkeeping system — as the system of record.

Three things happen here:

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

The organisation's books are Holded's. Finance only ever reads them, plus creates expense accounts
and member contacts; it never posts a journal entry.

## 2. The shapes

The contract's methods, grouped by the question each one answers.

| Shape | The question | Methods | Asked by |
|---|---|---|---|
| **A — Accounts to spend against** | Which categories have a Holded account, and make the missing ones | `GetProvisioningPlanAsync`, `ProvisionAsync`, `GetHoldedAccountIdForCategoryAsync` | Finance's own page; Expenses, to book a line |
| **B — What was spent** | Pull the invoices; the per-category total, what didn't attribute, and how the pull went | `SyncAsync`, `GetActualsForYearAsync`, `GetUnmatchedAsync`, `GetDocSyncInfoAsync` | Nightly job; Budget's year page; Finance's own page; the Holded admin screen |
| **C — Whose account is this** | Which Holded creditor account is this member's — set it, correct it, clear it | `GetCreditorContactByUserAsync`, `EnsureCreditorContactAsync`, `SetCreditorAccountNumAsync`, `SetCreditorContactAsync`, `ClearCreditorContactAsync` | Expenses' push path; Finance's own page |
| **D — What is owed** | What does the org owe this member, and what is the journal behind it | `GetCreditorStatusAsync`, `GetCreditorLedgerAsync`, `ListCreditorAccountsAsync` | Expenses' member and admin views; Finance's own page |

What the grouping shows:

- **C is the section's real difficulty, and it is essential.** Three writers give deliberately
  different answers to the same collision, because only some of them are guesses. Not collapsible;
  see load-bearing weirdness.
- **D is three resolutions of one derivation.** All three start from the same cached journal lines
  and the same cached Holded contact list, and compute balance and owed the same way. The shapes
  differ because the callers differ; the arithmetic and the contact lookup must exist once.
- **B's `GetDocSyncInfoAsync` carries a C fact.** Its result includes a count of creditor bindings,
  which is not sync state. One screen's convenience shaped a contract type.
- **The section is read-heavy but exposes one read/write interface.** Every consumer of a creditor
  balance also holds `ProvisionAsync` and `ClearCreditorContactAsync`. The repo's stated pattern for
  cross-section calls is a read interface (`peters-hard-rules.md`, Patterns); Finance has none. Seam,
  not a strike — see §5.

## 3. Structure

The layout those four shapes imply:

```
Humans.Finance.Contracts/      one interface, the DTOs its methods return
Humans.Finance/
  Section.cs                   DI
  Controllers/                 one controller — three pages and their four posts
  Models/                      view models for those pages
  Views/Finance/               those pages
  Services/
    Service.cs                 the four shapes
    HoldedMatcher.cs           pure attribution, no dependencies
  Domain/                      four entities, two enums
  Data/                        one repository over one context, four tables
```

That is what is there. The file structure is right; the work is inside the files, not between them.

Three things the shapes say about the inside:

- Shape **D**'s three methods share one derivation of balance and owed from a set of cached journal
  lines, and one accessor for the cached Holded contact list. A fourth path to either is an
  inconsistency, not a variation.
- Shape **C**'s collision policy is one rule with three call-site answers. The rule lives in one
  predicate; each writer states its own answer beside its own write. Not three rules.
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
- Every read that draws on Holded's contact list is filtered to the `40000000`–`40000999` block.
  Outside that block a supplier number belongs to an ordinary vendor, not a member.
- Balance keeps Holded's sign everywhere except the two admin views, which flip it once for display
  so a positive figure is money owed to the member.
- A Holded outage costs account *names*, never a page. Anything that is not a vendor failure throws.
- Finance reads Holded's journal; it never writes one.
- Finance touches no Budget or Tickets table — only their contracts.

## 5. Seams

Specified, not built. Not ranked, not struck; items touching these callers are shaped by them.

- **A read interface for cross-section consumers.** Shapes B and D are what other sections actually
  call; shape A and C's writes are Finance's own page and Expenses' push. The repo pattern says a
  read interface. Needs Peter — public-surface addition.
- **Line-level attribution.** A multi-line invoice booked across several Holded accounts lands wholly
  on the first line's category. Deliberate v1 simplification, still true.
- **No retry on creditor-number resolution** (nobodies-collective/Humans#972). The one-shot lookup
  misses and nothing tries again; the unresolved card exists because of it.
- **Binding writes can still lose a concurrent unbind** (nobodies-collective/Humans#995) outside the
  steady state. The re-read before write shrinks the window; closing it needs an update-only
  repository write.
- **`holded_*` tables under a section called Finance** (nobodies-collective/Humans#1012). A rename is
  schema work, deferred wholesale.

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
  an attribution that does not exist.
- **No nullable `Balance` on the creditor DTOs.** Both reads return null wholesale when an account has
  no cached lines, so a returned status or ledger always has a balance. `HoldedCreditorAccountRow`
  keeps its `decimal?` — the admin list does carry accounts with no lines yet.
- **No `Name` on `HoldedCreditorLedger`.** It was exactly `Contact?.Name`; the statement header reads
  through the contact (Peter, 2026-08-18).
- **No split of `Service.cs` into per-shape services.** The four shapes share the repository, the
  clock and the contact cache; splitting would trade one long file for four files and a fifth
  coordinating them.

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

## History

| Run | Anchor | Headline | PR |
|---|---|---|---|
| [2026-08-18](../../../../docs/health/runs/2026-08-18-Finance.md) | `41fd7374d` | First target. Doc led with 23 routes the section does not serve; a tag-collision bug in provisioning; a published DTO with no consumer. Prod code −113 lines, tests 55 → 92, mutation 34.4% → 57.9%. | [#1374](https://github.com/peterdrier/Humans/pull/1374) |
