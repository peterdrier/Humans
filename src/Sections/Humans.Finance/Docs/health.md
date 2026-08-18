# Finance — Health

Last assessed: 2026-08-18 @ 485a4714b (section-doctor, first scheduled run)

## Scorecard

| Axis | State |
|---|---|
| Reforge (section) | 254 — mid-pack, unchanged by this run (nothing structural was taken). Two items carry most of it: `Service` is over the `largeClass` line threshold, and `IHoldedFinanceService` is a full-service interface carrying the section's own admin operations alongside the cross-section reads. Neither is dead weight — see Ideal shape |
| Tests | `Humans.Finance.Tests`, all sub-second. The creditor-binding invariants are covered unusually well — every write path, including every concurrency window nobodies-collective/Humans#995 named. Stryker: n/a, not run this run |
| Docs vs code | **The worst axis, and the run's whole first half.** `Finance.md` still described the pre-G5 controller: Budget-CRUD routes it does not serve, a `POST /Finance/Creditors/Resync` removed with the Holded v2 split, an `ITicketServiceRead` cash-flow dependency the section has not had, a Budget read-split listed as future work that already shipped, plus wrong file locations and stale class names. Fixed, and the duplicated Budget route table replaced by a pointer to `Budget.md` so it cannot drift again |
| Comments / slop | Inverted from the usual finding: not slop but **volume**. `Service.cs` carries multi-paragraph rationale blocks with decision history and issue archaeology inline — against `comments-stay-short` (1–3 lines, rationale in the issue). Every one is accurate and most restate an invariant that is also in `Finance.md` |
| GUI / nav | Sound. Four admin views, each with a working backlink; `CreditorStatement` links back to both `/Finance/Creditors` and the account's general ledger on `/Holded`. No dead ends |
| Translations | None, deliberately. English-only finance-admin pages, zero `Localizer` call sites, no `.resx` — recorded in the section doc |
| Arch conformance | Clean. `Service` and `Repository` are `internal`, one repository owns every table the section holds, cross-section calls all go through contracts leaves, and the Budget dependency is already the read half. No grandfathers, no obsoletes, no cross-section DbContext reads |

## Ideal shape

Rewritten today, Finance would be **two services, not one**. `Service` is a single class doing
provisioning, purchase-doc sync, actuals aggregation, creditor bindings, ledger derivation and
the GDPR export contribution. Those split cleanly along a real seam that already exists in the
data: the **doc pipeline** (`holded_expense_docs`, `holded_category_map`,
`holded_doc_sync_state` — a nightly full-pull, attribution and an unmatched queue) and the
**creditor bindings** (`holded_creditor_contacts` — a member↔account link with a three-way
concurrency story). They share no state and no invariant; the only thing joining them is that
both talk to Holded. The oversized class and the wide interface are both symptoms of that one
merge.

The public contract would follow the split rather than lead it. `GetProvisioningPlanAsync`,
`ProvisionAsync`, `GetUnmatchedAsync`, `SetCreditorContactAsync` and `ClearCreditorContactAsync`
exist on a public cross-assembly contract solely so `FinanceController`, in the same project, can
call them; everything else on it has a real cross-section caller.

The third thing a rewrite would not keep is **the rationale living in the code**. The concurrency
invariants around the binding write paths are genuinely subtle and genuinely worth writing down,
and they are written down twice: once as `Finance.md` invariants and once as multi-paragraph
comment blocks above the methods. One of those is the right place.

None of it is a defect, and the section works. This is the shape a rewrite would take.

## Opportunities (ranked by value)

1. **Split `Service` along the doc-pipeline / creditor-bindings seam** (the ideal-shape move).
   Retires both reforge findings at once and is behaviour-preserving. Needs Peter: it adds a
   type and a DI registration.
2. **Take the admin-only methods off the public `IHoldedFinanceService`.** Needs Peter — the
   natural landing place is a new internal interface, which is surface *addition*.
3. **`RawPayload` is a NOT NULL jsonb column that has only ever held `{}`.** `MapDoc` never
   wrote a payload and nothing reads it. Dropping it is a schema change, so it is queued.
4. **Trim the `Service.cs` rationale blocks to 1–3 lines each, pointing at the `Finance.md`
   invariant that already carries the argument.** Needs Peter: the judgment is whether the doc
   is genuinely the right home for all of it.
5. **Contract properties InspectCode reports as never read** —
   `HoldedCreditorStatus.SupplierAccountNum`, `HoldedPaymentInfo.DocumentType`,
   `HoldedUnmatchedRow.HoldedDocId`, `CreditorContactBinding.HoldedContactId`,
   `CreditorLedgerLine.AccountNum`, `HoldedMatchEntry.AccountNum`. Deliberately not deleted:
   on a contract record, "no consumer reads it today" is weak evidence that it should not be
   carried, and two of them are the natural key of the row they sit on.

## History

| Date | Reforge | Outcome | PR |
|---|---|---|---|
| 2026-08-18 | 254 | Section doc described the pre-G5 controller — phantom Budget routes, a removed Resync route, a phantom Tickets dependency, a shipped read-split still listed as future work, wrong file paths, stale class names; swept the same claims out of `FinanceController`, `Section.cs`, `IHoldedNightlySync` and `docs/sections/_Index.md`. Pinned the Madrid date conversion and the 2-minute contact cache, neither of which had a test. Two InspectCode findings | peterdrier/Humans#1367 |
