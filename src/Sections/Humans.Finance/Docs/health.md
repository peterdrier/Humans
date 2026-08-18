# Finance — Health

Last assessed: 2026-08-18 @ 485a4714b (section-doctor, first scheduled run)

## Scorecard

| Axis | State |
|---|---|
| Reforge (section) | 254, rank 15 of 38 — mid-pack, unchanged by this run (nothing structural was taken). Two items carry most of it: `Service` at 856 lines is over the 750-line `largeClass` threshold, and `IHoldedFinanceService` is a fourteen-method full-service interface. Neither is dead weight — see Ideal shape |
| Tests | 57 in `Humans.Finance.Tests` (55 before run), all sub-second. The creditor-binding invariants are covered unusually well — 31 tests across the three write paths, including every concurrency window #995 named. Stryker: n/a, not run this run |
| Docs vs code | **The worst axis, and the run's whole first half.** `Finance.md` still described the pre-G5 controller: 23 Budget-CRUD routes it does not serve, a `POST /Finance/Creditors/Resync` removed with the Holded v2 split, an `ITicketServiceRead` cash-flow dependency the section has not had, a Budget read-split listed as future work that already shipped, two wrong file locations and three stale class names. Fixed, and the duplicated Budget route table replaced by a pointer to `Budget.md` so it cannot drift again |
| Comments / slop | Inverted from the usual finding: not slop but **volume**. `Service.cs` carries roughly fifteen multi-paragraph rationale blocks, several 10–14 lines, with decision history and issue archaeology inline — against `feedback_comments_stay_short` (1–3 lines, rationale in the issue). Every one is accurate and most restate an invariant that is also in `Finance.md`. Queued, not touched |
| GUI / nav | Sound. Four admin views, each with a working backlink; `CreditorStatement` links back to both `/Finance/Creditors` and the account's general ledger on `/Holded`. No dead ends |
| Translations | None, deliberately. English-only finance-admin pages, zero `Localizer` call sites, no `.resx` — recorded in the section doc |
| Arch conformance | Clean. `Service` and `Repository` are `internal`, one repository owns all four tables, cross-section calls all go through contracts leaves, and the Budget dependency is already the read half. No grandfathers, no obsoletes, no cross-section DbContext reads |

## Ideal shape

Rewritten today, Finance would be **two services, not one**. `Service` is a single class doing
provisioning, purchase-doc sync, actuals aggregation, creditor bindings, ledger derivation and
the GDPR export contribution. Those split cleanly along a real seam that already exists in the
data: the **doc pipeline** (`holded_expense_docs`, `holded_category_map`,
`holded_doc_sync_state` — a nightly full-pull, attribution and an unmatched queue) and the
**creditor bindings** (`holded_creditor_contacts` — a member↔account link with a three-way
concurrency story). They share no state and no invariant; the only thing joining them is that
both talk to Holded. The 856-line class and the fourteen-method interface are both symptoms of
that one merge.

The public contract would follow the split rather than lead it. Nine of the fourteen methods have
callers outside this project; the other five — `GetProvisioningPlanAsync`, `ProvisionAsync`,
`GetUnmatchedAsync`, `SetCreditorContactAsync`, `ClearCreditorContactAsync` — exist on a public
cross-assembly contract solely so `FinanceController`, in the same project, can call them.

The third thing a rewrite would not keep is **the rationale living in the code**. The concurrency
invariants around the three binding write paths are genuinely subtle and genuinely worth writing
down, and they are written down twice: once as `Finance.md` invariants and once as 10–14 line
comment blocks above the methods. One of those is the right place.

None of the three is a defect, and the section works. This is the shape a rewrite would take,
ranked below.

## Opportunities (ranked by value)

1. **Split `Service` along the doc-pipeline / creditor-bindings seam** (the ideal-shape move).
   Retires both reforge findings at once and is behaviour-preserving. Needs Peter: it adds a
   type and a DI registration.
2. **Take the five admin-only methods off the public `IHoldedFinanceService`.** The contract
   Budget, Expenses and Holded actually consume is nine methods. Needs Peter — the natural
   landing place is a new internal interface, which is surface *addition*.
3. **`RawPayload` is a NOT NULL jsonb column that has only ever held `{}`.** `MapDoc` never
   wrote a payload and nothing reads it. Dropping it is a schema change, so it is queued.
4. **Trim the `Service.cs` rationale blocks to 1–3 lines each, pointing at the `Finance.md`
   invariant that already carries the argument.** Roughly 200 lines. Needs Peter: the judgment
   is whether the doc is genuinely the right home for all of it.
5. **Six contract properties InspectCode reports as never read** —
   `HoldedCreditorStatus.SupplierAccountNum`, `HoldedPaymentInfo.DocumentType`,
   `HoldedUnmatchedRow.HoldedDocId`, `CreditorContactBinding.HoldedContactId`,
   `CreditorLedgerLine.AccountNum`, `HoldedMatchEntry.AccountNum`. Deliberately not deleted:
   on a contract record, "no consumer reads it today" is weak evidence that it should not be
   carried, and two of them are the natural key of the row they sit on.

## History

| Date | Reforge | Tests | Outcome | PR |
|---|---|---|---|---|
| 2026-08-18 | 254 | 57 | Section doc described the pre-G5 controller — 23 phantom routes, a removed Resync route, a phantom Tickets dependency, a shipped read-split still listed as future work, two wrong file paths, three stale class names; swept the same claims out of `FinanceController`, `Section.cs`, `IHoldedNightlySync` and `docs/sections/_Index.md`. Pinned the Madrid date conversion and the 2-minute contact cache, neither of which had a test. Two InspectCode findings | peterdrier/Humans#PENDING |
