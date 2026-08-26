# Expense reimbursement — the end-to-end business process

**This is a business-process doc, not a code spec.** It describes how money actually moves from
"a member paid for something" to "the books add up to zero", including the manual steps, the
external parties, and who wears which hat. Code invariants live in [`Finance.md`](Finance.md) and
[`features/sepa-payout.md`](features/sepa-payout.md); when this doc and the code disagree about
behavior, the code docs win — when it's about *process*, this doc wins.

Finance is the section where the outside world shows up: **Holded** (accounting system, shared
with the accountant), **Banco Sabadell** (the bank), **Pleo** (cards), the **accountant**, and
behind them the **Spanish state** (an asociación's books are legal records). The treasurer —
currently Peter — is the human in the loop for everything that touches real money.

## The process

Happy path. When something needs fixing it gets more complicated.

1. **A member enters an expense report** (`/Expenses`).
2. **The department coordinator approves it**, setting a max if the report goes over what's
   allocated.
3. **The treasurer (finance admin) also approves it** — second pair of eyes on every report.
4. **The report is uploaded to Holded** for the accountant, and because math. The member gets a
   Holded contact with a creditor account (e.g. `40000004`).
5. **Holded tells Humans** that the member with that account is now owed, say, €132.45. Visible
   at the top of `/Expenses` and on `/Finance/Creditors`.
6. **On pay day the treasurer selects who gets paid** on `/Finance/Creditors` and generates a
   **SEPA file** (pain.001.001.09) — a bank file saying pay Sally 123, Joe 243, Fred 321, to the
   right IBANs.
7. **The file is uploaded to the bank** by the treasurer plus a second verifier, checking the
   totals (3 payments, totaling ####). Then the money goes bye-bye.
   - **7-A.** The member receives the money in their account. This can take a day or two —
     Spain isn't fast.
8. **The bank movements sync back to Holded** as individual transfers, for more accounting math
   fun.
9. **Humans gets the update from Holded** (nightly ledger sync): "the bank sent €123 to Sally".
10. **Humans tells Holded which creditor account each transfer settles** — the Book action on
    `/Finance/Sepa` posts the payment against the member's open purchase docs, so the €123 lands
    on account `40000004` and the math adds up to zero.
11. **When it all adds up to zero, we're done.** The balance disappears from `/Expenses` and
    `/Finance/Creditors`.

## Why it's built this way

It's not exactly cake, but the shape is deliberate: **appropriate controls where we could get
robbed, automation everywhere else.**

- Two human approvals (steps 2–3) before anything reaches the books.
- Two humans verify the bank upload (step 7) — the one step that irreversibly moves money stays
  manual and double-checked.
- Everything around those control points is automated: the Holded push, the balance reads, the
  SEPA file generation, the settlement booking, the sync loops. Nothing depends on a human
  remembering to do bookkeeping.
- Holded is the source of truth for what's owed (the balance is derived from its daybook, never
  tracked in Humans), and Humans never writes journal entries by hand — settlement goes through
  Holded's own payment API so the accountant sees a normal ledger.

## Status (2026-08-26)

Steps 1–8 are live in production. Steps 9–11 — the booking screen (`/Finance/Sepa`,
nobodies-collective/Humans#1141) — are on QA under test, verified with a real €50 payout the
treasurer sent himself.
