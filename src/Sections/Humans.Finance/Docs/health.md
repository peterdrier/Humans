# Finance — Health

Last assessed: 2026-08-18 @ 485a4714b (section-doctor, first scheduled run)

## Scorecard

| Axis | State |
|---|---|
| Reforge (section) | 215 after this run, down from 254. The two findings that carried it — an oversized service and a wide full-service interface — were one problem, and splitting the service along its real seam retired both |
| Tests | `Humans.Finance.Tests`, all sub-second, split to match the services. The creditor-binding invariants are covered unusually well: every write path, including every concurrency window nobodies-collective/Humans#995 named. Stryker: n/a, not run this run |
| Docs vs code | **Was the worst axis and the run's whole first half.** `Finance.md` still described the pre-G5 controller: Budget-CRUD routes it does not serve, a `POST /Finance/Creditors/Resync` removed with the Holded v2 split, an `ITicketServiceRead` cash-flow dependency the section has not had, a Budget read-split listed as future work that already shipped, plus wrong file locations and stale class names. Fixed, and the duplicated Budget route table replaced by a pointer to `Budget.md` so it cannot drift again |
| Comments / slop | Was volume, not slop: multi-paragraph rationale blocks with decision history inline, against `comments-stay-short`, restating invariants `Finance.md` already carried. Trimmed to the constraint plus a pointer to the invariant |
| GUI / nav | Sound. Four admin views, each with a working backlink; `CreditorStatement` links back to both `/Finance/Creditors` and the account's general ledger on `/Holded`. No dead ends |
| Translations | None, deliberately. English-only finance-admin pages, zero `Localizer` call sites, no `.resx` — recorded in the section doc |
| Arch conformance | Clean. Services and repository `internal`, one repository over every table the section holds, cross-section calls all through contracts leaves, Budget dependency already the read half. No grandfathers, no obsoletes, no cross-section DbContext reads |

## Ideal shape

Reached, in this run. Finance is now two services along the seam its data already had — the
**doc pipeline** (`holded_expense_docs`, `holded_category_map`, `holded_doc_sync_state`: a nightly
full-pull, attribution, an unmatched queue) and the **creditor bindings**
(`holded_creditor_contacts`: a member↔account link with a three-way concurrency story). They share
no state and no invariant, only the section's one repository.

The public contract follows the split rather than leading it: each service is registered once and
exposed twice, a contracts-leaf interface carrying only what other sections call and a wider
internal one only `FinanceController` resolves. Provisioning, the unmatched queue and bind/unbind
no longer cross an assembly boundary.

The rationale that used to live in the code now lives in `Finance.md`, where it was already
written a second time; the comments state the constraint and point at it.

What a rewrite would still not keep is `GetDocSyncInfoAsync` returning the creditor binding count
from the doc service — see below.

## Opportunities (ranked by value)

1. **`GetDocSyncInfoAsync` is the split's one leak.** A doc-pipeline method that also returns the
   creditor binding count, so `/Holded` can show the number without building the full creditor
   view. Legal — both services share the section's one repository — but the figure belongs to the
   creditor half, and a caller wanting both should ask both.
2. **`HoldedPaymentInfo` is a public contracts type that never crosses the boundary.** It is
   constructed and consumed entirely inside `CreditorService.LedgerPayments`, which returns only
   the aggregates. It wants to be private to the service. Needs Peter — it is a public type deletion.
3. **Contract properties InspectCode reports as never read**, with the judgment on each:
   `HoldedCreditorStatus.SupplierAccountNum` echoes back a value the only caller passed in;
   `HoldedUnmatchedRow.HoldedDocId` is redundant with the `HoldedUrl` built from it;
   `CreditorLedgerLine.AccountNum` is constant across a statement, which renders one account;
   `CreditorContactBinding.HoldedContactId` is half the identity of the at-most-one-member
   invariant and should probably stay even though nothing reads it today.
   (`HoldedMatchEntry.AccountNum` was internal and plainly dead — deleted this run.)

## History

| Date | Reforge | Outcome | PR |
|---|---|---|---|
| 2026-08-18 | 254 → 215 | Section doc described the pre-G5 controller — phantom Budget routes, a removed Resync route, a phantom Tickets dependency, a shipped read-split still listed as future work, wrong file paths, stale class names; swept the same claims out of `FinanceController`, `Section.cs`, `IHoldedNightlySync` and `docs/sections/_Index.md`. Pinned the Madrid date conversion and the contact-cache window, neither of which had a test. Then, on Peter's go: `Service` split into `HoldedDocService` + `CreditorService` with the public contract narrowed to what other sections call, `RawPayload` dropped, and the rationale blocks trimmed to the constraint | peterdrier/Humans#1367 |
