# Expenses — target shape

Derived fresh each section-doctor run, before any scan. The invariant doc is
`Expenses.md`; this file is what the section *should* look like, plus its health history.

## 1. What the section does

A member claims money back. They build a claim out of line items — a receipt they paid for,
or an invoice a contractor sent the association — attach the paperwork to each one, and send
it in. If the budget the claim is booked against has a coordinator, that coordinator vouches
for it first. Finance then reads the claim with the paperwork in front of them and either
approves it, caps it at a lower figure, or sends it back with a reason.

Approving it books it into the association's accounting system as a bill the association owes
that member. That is where this section's authority ends: nobody here marks anything paid.
Whether the member has actually been paid is read back out of the accounting ledger and
shown to them, and it is the treasurer's bank, not this system, that moves the money.

Everything a person does on someone else's behalf leaves a trail naming both of them, and the
member's bank account number is masked everywhere except the two places that need it whole:
the accounting system's own API call, and the audit row that records who typed it.

## 2. The shapes

Every question the section answers, and how it answers it.

| Question shape | Asked by | Answered by |
|---|---|---|
| What claims are mine, and where do I stand with the association? | member | `/Expenses` |
| What is on this claim, and what may I do to it? | anyone who may see it | `/Expenses/{id}` |
| Change the claim's header / lines / files | owner in Draft, finance any time pre-approval | `/Expenses/{id}/Edit` + `Lines/*` |
| What is behind this invoice? | reviewer, submitter | `/Expenses/{id}/Lines/{lineId}/Proofs` |
| Which account gets paid for this claim? | submitter, finance | `/Expenses/{id}/Iban` |
| Show me that receipt | anyone who may see the claim | `/Expenses/Attachment/{id}[/View]` |
| What is waiting on me? | member, coordinator, finance — one queue, scoped | `/Expenses/Review` |
| Move the claim along | submitter / coordinator / finance | Submit, Withdraw, Endorse, Approve, both Rejects |
| Did this claim reach the accounting system? | finance | Holded sync card + `HoldedRetry` |
| Push approved claims into accounting | nobody — the clock | `HoldedExpenseOutboxJob` → outbox drain |
| Everything this member's claims hold | GDPR export | `IUserDataContributor` |

Two structural facts follow from the table. **Every decision is taken from the claim's own
page**, never from a queue row — the queue lists and links, it does not decide. And **the
outbox is the only writer that is not a person**, which is why it is the only path with
retries, a backoff and a write-off.

## 3. Structure

What the shapes imply, written fresh:

- **One page per question.** A controller action per row above, each one: resolve the actor,
  load the claim, ask the authorization handler, hand off to the service, redirect. No branch
  in a controller that the handler could have answered.
- **One authorization handler** that owns the actor × operation × status matrix, and one
  operation per thing a person can do. Nothing hand-rolls an ownership check beside it.
- **One service** holding the state machine, with one method per transition. A transition
  method validates, calls exactly one repository write, and writes exactly one audit entry.
- **One repository** owning the four tables, each write atomic, returning DTOs only.
- **The outbox drain is its own concern** inside the service — queue semantics in one place,
  the Holded conversation in another, and a scheduler shim that holds neither.
- **DTOs are the only thing that crosses out.** The section's public surface is what another
  section actually consumes, and nothing wider.

Where today's layout departs from that: mutations exist twice (an `internal XxxAsync` that
throws and a `public XxxWithResultAsync` that catches), the controller repeats a five-line
load-and-authorize preamble in fourteen actions, and the badge-colour map for one enum is
written out in two files.

## 4. Invariants

Stated so a violation is recognisable. The authoritative list is `Expenses.md`; these are the
ones a change is most likely to break silently.

- A claim belongs to its `SubmitterUserId`. Everything payee-shaped reads the *submitter's*
  profile; the actor appears only in audit rows and `UploadedByUserId`.
- `Approved` is terminal. No payment state is ever stamped on a claim.
- `Payable = min(Total, MaxAmount)` is the only figure payment math may use; `Total` is the
  receipts total and renders as nothing else.
- Only a decider sets `MaxAmount`, on their own decision form, and it is recorded in that
  decision's audit entry.
- Proof rows never reach `Total` and never reach Holded — not as document lines, not as files.
- A header edit never moves a claim between budget years.
- Raw IBAN appears only in a Holded request body and in an audit row whose subject is not the
  actor. Everywhere else it is `IbanFormatter.Mask`ed.
- The drain does nothing when no Holded key is configured, and a written-off push is visible
  on both `/Expenses/Review` and the claim, never silently dropped.
- Attachment pushes are stamped, so a re-drain resumes rather than duplicating files.

## 5. Seams — specified but unbuilt

- **Travel lines (Mileage / PerDiem) cannot be created.** The forms and endpoints are gone;
  the service methods, enum members, `PerDiemKind` and `TravelReimbursementConfig` remain so
  existing lines still render, total and submit. Turning it back on is restoring two
  controller actions and two forms. Retained deliberately — not dead code to reap.
- **Deleting a Draft.** `ExpenseRepository.WithdrawAsync` refuses Draft and its comment names
  a "Delete-while-Draft when that ships". Nothing ships it; a draft is abandoned, not removed.
- **Recategorise after push.** `UpdateIncomingDocTag` outbox events drain to a log line —
  Holded v2 has no tag endpoint, so the correction is made inside Holded and mirrored back.
  The enum member survives so pre-existing rows drain instead of poisoning the queue.

## 6. Deliberately not done

- **No caching decorator.** Claim data is mutable and per-user, at ~500 users.
- **No pagination anywhere.** `GetAllAsync` loads every claim and sums client-side, by design.
- **No enforcement of proof coverage against the invoice amount.** VAT and fees mean the two
  legitimately differ; the detail page shows both and stops there.
- **No SEPA generation, no paid flag.** Payment left this section on purpose
  (nobodies-collective/Humans#1134); `/Finance/Creditors` operates on balances.
- **No re-reading of audit for the report history page.** The section emits
  `<vc:audit-log>` and lets AuditLog own the read and the render.
- **No concurrency token on a claim.** Repo-wide rule.

## Load-bearing weirdness

Settled decisions that read as accidents. Do not re-litigate these.

- **`Approved` is terminal, and paid/unpaid is derived from the Holded creditor balance.**
  Blending local claim rows into that ledger nets a local claim against a Holded debit whose
  matching credit is never shown — the reason the old "IOU ledger" card was removed.
- **The negative adjustment line on a capped push.** The receipts book in full and one
  negative line brings the document down to the payable, so the receipt lines are never
  rewritten to match a cap.
- **A finance admin's edit does not send a claim back a step**, and the edit window closes at
  approval because the Holded push is queued in that same transaction.
- **`IbanSet` audit rows written by somebody else carry the IBAN unmasked** — the one
  exception to the masking rule, so a wrongly-typed account traces to who typed it.
- **`ExpenseSepaSent` / `ExpenseSepaReopened` / `ExpensePaid` remain in the GDPR export's
  action list** although nothing writes them. The audit log is immutable; only the writers
  went away.
- **The review queue renders in the admin shell for finance and the member shell for everyone
  else.** An admin sidebar filtered down to nothing is worse than no sidebar.
- **`Humans.Expenses` references `Humans.Budget` and `Humans.AuditLog` themselves**, not only
  their contracts leaves, because a view component and a resource key live in the section
  projects. Both directions are checked acyclic.

## Health history

| Run | Date | Reforge (surface) | loc | cogP95 / cogMax | PR |
|---|---|---|---|---|---|
| 1 | 2026-08-26 | 287 | 4094 | 9 / 20 | pending |
