# Expenses — target shape

Derived fresh each section-doctor run, before any scan. The invariant doc is
`Expenses.md`; this file is what the section *should* look like, plus its health history.

## 1. What the section does

A member claims money back. They build a claim out of line items — a receipt they paid for,
or an invoice a contractor sent the association — attach the paperwork to each one, and send
it in. If the budget the claim is booked against has a coordinator, that coordinator is meant
to vouch for it first. Finance then reads the claim with the paperwork in front of them and
either approves it, caps it at a lower figure, or sends it back with a reason.

Approving it tells the member by email and books the claim into the association's accounting
system: one bill per receipt, owed to that member, trimmed so the bills add up to what was
authorized. That is where this section's authority ends: nobody here marks anything paid.
Whether the member has actually been paid is read back out of the accounting ledger and shown
to them, and it is the treasurer's bank, not this system, that moves the money.

Everything a person does on someone else's behalf leaves a trail naming both of them, and the
member's bank account number is masked everywhere it is written out, except the places that need
it whole: the accounting system's own API call, and the audit row that records who typed it.

## 2. The shapes

Every question the section answers, and how it answers it.

| Question shape | Asked by | Answered by |
|---|---|---|
| What claims are mine, and where do I stand with the association? | member | `/Expenses` |
| What is on this claim, and what may I do to it? | anyone who may see it | `/Expenses/{id}` |
| Change the claim's header / lines / files | owner in Draft, finance any time pre-approval | `/Expenses/{id}/Edit` + the `/Expenses/{id}/Lines/...` pages |
| What is behind this invoice? | reviewer, submitter | `/Expenses/{id}/Lines/{lineId}/Proofs` |
| Which account gets paid for this claim? | submitter, finance | `/Expenses/{id}/Iban` |
| Show me that receipt | anyone who may see the claim | `/Expenses/Attachment/{id}[/View]` |
| What is waiting on me? | member, coordinator, finance — one queue, scoped | `/Expenses/Review` |
| Move the claim along | submitter / coordinator / finance | Submit, Withdraw, Endorse, Approve, both Rejects |
| Was my claim approved, and for how much? | member | the `expense_approved` email (`ExpensesEmails`) |
| Did this claim reach the accounting system? | finance | Holded sync card + `HoldedRetry` |
| Push approved claims into accounting | nobody — the clock | `HoldedExpenseOutboxJob` → outbox drain |
| Everything this member's claims hold | GDPR export | `IUserDataContributor` |

Structural facts follow from the table. **Every decision is taken from the claim's own
page**, never from a queue row — the queue lists and links, it does not decide. **The
outbox is the only writer that is not a person**, which is why it is the only path with
retries, a backoff and a write-off. And **every question is asked from inside the section**:
no other section reads a claim.

## 3. Structure

What the shapes imply, written fresh:

- **One page per question.** A controller action per row above, each one: resolve the actor,
  load the claim, ask the authorization handler, hand off to the service, redirect. No branch
  in a controller that the handler could have answered.
- **One authorization handler** that owns the actor × operation × status matrix, and one
  operation per thing a person can do. What the handler grants, the service accepts; nothing
  hand-rolls an ownership check beside it.
- **One service** holding the state machine, with one method per transition. A transition
  method validates, calls exactly one repository write, and writes an audit entry per auditable
  action it took — normally one, and two where approval also overrides the category.
- **One repository** owning the section's tables, each write atomic, returning DTOs only.
- **The outbox drain is its own concern** inside the service — queue semantics in one place,
  the Holded conversation in another, a scheduler shim that holds neither — and **one cap
  allocation** (`PayableAllocation`) that the push, the detail page and the audit text all read.
- **The public surface is what another section consumes.** Nothing outside the section reads
  a claim today, so the cross-section read interface has no reader; the section needs only
  `Section` and the background-processor seam the job calls.

Where today's layout departs from that: mutations exist twice (an `internal XxxAsync` that
throws and a `public XxxWithResultAsync` that catches), the controller repeats a
load-and-authorize preamble in nearly every action that takes a report id, and the handler
grants finance admins `Endorse` where the service refuses anyone who does not coordinate the
category — see the run files.

## 4. Invariants

Stated so a violation is recognisable, each with the line that enforces it. The authoritative
list is `Expenses.md`; these are the ones a change is most likely to break silently.

- **The payee is the submitter, snapshotted.** Submit copies the *submitter's* profile IBAN and
  legal name into the claim (`Services/ExpenseReportService.cs:750`), never the actor's; the
  Holded push pays from that snapshot. `/Expenses/{id}/Iban` refreshes it only while the claim
  is pending approval (`Services/ExpenseReportService.cs:839`).
- **Approved closes the claim.** Approve and both rejects accept only Submitted or
  CoordinatorEndorsed (`Data/ExpenseRepository.cs:296`, `Data/ExpenseRepository.cs:328`);
  endorse and coordinator-reject only Submitted (`Data/ExpenseRepository.cs:260`,
  `Data/ExpenseRepository.cs:278`); the only move out of Approved is Withdrawn
  (`Data/ExpenseRepository.cs:245`). No payment state is ever stamped on a claim.
- **Payable is the only figure payment math uses** — `min(Total, MaxAmount)`
  (`Contracts/ExpenseReportDto.cs:19`).
- **A decider's form replaces the cap outright**, blank clears it
  (`Data/ExpenseRepository.cs:262`, `Data/ExpenseRepository.cs:306`); neither reject touches it.
- **Proof rows never reach `Total`** (`Data/ExpenseRepository.cs:117`) and never reach Holded —
  the allocation skips them (`Services/PayableAllocation.cs:29`).
- **The cap allocates greedily in line order**, and a line past the cap gets no Holded doc and
  no upload (`Services/ExpenseReportService.cs:1460`).
- **A pushed line is never pushed twice.** A line's doc id is written the moment Holded issues
  it (`Services/ExpenseReportService.cs:1505`), an upload is stamped
  (`Services/ExpenseReportService.cs:1562`), and a legacy single-doc claim resumes onto its one
  doc (`Services/ExpenseReportService.cs:1385`).
- **A header edit never moves a claim between budget years** once submitted
  (`Services/ExpenseReportService.cs:314`).
- **Masking is a rule about output, not storage.** The approval email and the GDPR export carry
  the masked form (`Services/ExpenseReportService.cs:1113`, `Services/ExpenseReportService.cs:1682`);
  the one unmasked write is an IBAN audit row whose actor is not its subject
  (`Services/ExpenseReportService.cs:937`).
- **The drain does nothing without a Holded key** (`Services/ExpenseReportService.cs:1210`), and a
  written-off push is counted on `/Expenses/Review` (`Data/ExpenseRepository.cs:370`).

## 5. Seams — specified but unbuilt

- **Travel lines (Mileage / PerDiem) cannot be created.** The forms and endpoints are gone;
  the service methods, enum members, `PerDiemKind` and `TravelReimbursementConfig` remain so
  existing lines still render, total and submit. Turning it back on is restoring the
  controller actions and their forms. Retained deliberately — not dead code to reap.
- **Deleting a Draft.** `ExpenseRepository.WithdrawAsync` refuses Draft; a draft is abandoned,
  not removed.
- **Recategorise after push.** `UpdateIncomingDocTag` outbox events drain to a log line —
  Holded v2 has no tag endpoint, so the correction is made inside Holded and mirrored back.
  The enum member survives so pre-existing rows drain instead of poisoning the queue.
- **"Waiting on the coordinator" in the queue.** `CategoryRequiresCoordinatorEndorsementAsync`
  computes whether a category has a coordinator and has no production caller; it is reserved for
  telling the two review states apart, not for gating approval.

## 6. Deliberately not done

- **No caching decorator.** Claim data is mutable, per-user, and low-traffic.
- **No pagination anywhere.** `GetAllAsync` loads every claim and sums client-side, by design.
- **No enforcement of proof coverage against the invoice amount.** VAT and fees mean the
  figures legitimately differ; the detail page shows both and stops there.
- **No SEPA generation, no paid flag.** Payment left this section on purpose
  (nobodies-collective/Humans#1134); `/Finance/Creditors` operates on balances.
- **No re-reading of audit for the report history page.** The section emits
  `<vc:audit-log>` and lets AuditLog own the read and the render.
- **No concurrency token on a claim.** Repo-wide rule.
- **No per-line category.** Where a cap's reduction lands is presentation only; the whole claim
  books to one category account.

## Load-bearing weirdness

Settled decisions that read as accidents. Do not re-litigate these.

- **`Approved` is terminal, and paid/unpaid is derived from the Holded creditor balance.**
  Blending local claim rows into that ledger nets a local claim against a Holded debit whose
  matching credit is never shown — the reason the old "IOU ledger" card was removed.
- **A trimmed line books its receipt at face value plus a negative adjustment line.** Each doc
  then matches its attached receipt, and the cap is visible inside Holded rather than hidden in a
  rewritten amount.
- **Two places a claim's Holded documents live.** Claims pushed before per-line docs keep their
  one doc id on the report; newer ones carry an id per line. `ExpenseReportDto.HoldedDocIds`
  folds the two so nothing downstream has to know.
- **A finance admin's edit does not send a claim back a step**, and the edit window closes at
  approval because the Holded push is queued in that same transaction.
- **`IbanSet` audit rows written by somebody else carry the IBAN unmasked** — the one
  exception to the masking rule, so a wrongly-typed account traces to who typed it.
- **A rejection leaves `MaxAmount` standing.** The cap is the last figure a decider authorized,
  and it survives back into Draft on purpose: it stands until a coordinator or finance admin
  changes it on their next decision form. Peter confirmed this 2026-08-27 — run 1's finding 1.
- **Coordinator endorsement is a route, not a gate.** The coordinator knows their department, so
  they are meant to vouch first; but the finance admin is the one who pays and may approve
  straight from `Submitted` when it is urgent. The audit entry, not a refusal, is the control.
  Peter confirmed this 2026-08-27 — run 1's finding 2.
- **`ExpenseSepaSent` / `ExpenseSepaReopened` / `ExpensePaid` remain in the GDPR export's
  action list** although nothing writes them. The audit log is immutable; only the writers
  went away.
- **The approval email is sent after the approval commits**, so a refused approval sends
  nothing; a member with no notification address is logged and skipped.
- **The review queue renders in the admin shell for admin-role users and the member shell for
  everyone else**, by the Shell's one layout rule (`docs/sections/admin-shell.md`); the sidebar
  filters itself.
- **`Humans.Expenses` references `Humans.Budget` and `Humans.AuditLog` themselves**, not only
  their contracts leaves, because a view component and a resource key live in the section
  projects. Both directions are checked acyclic.

## Health history

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-08-26 | Gates nothing asked for, removed or queued; docs trued to the code | peterdrier/Humans#1537 |
| 2 | 2026-09-26 | Endorse contradiction queued; Edit page, submit error and docs trued to the code | peterdrier/Humans#1827 |
