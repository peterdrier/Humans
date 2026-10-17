# 2026-10-17 — Creativity (art grants) feedback on the first season of Expenses

Conversation between Peter and a Creativity coordinator after the art-grant team ran its first reimbursement season on Humans (previously: Paloma's spreadsheets plus a zip of invoices, processed by hand). Recorded so the follow-ups trace back to what was actually said. Items marked *filed* link to their issue; the rest are pending Peter's call.

## What the team reported

1. **Per-receipt editing on the artist side.** Artists hand in reports with many receipts (one project had 125). When one receipt is unusable (a handwritten note, an odd screenshot) the artists believed they had to delete the whole report and re-upload everything. The per-line page (`/Expenses/{id}/Lines/{lineId}`: edit, replace file, remove line) exists and a rejected report goes back to `Draft`, so the capability is there. The artists did not find it. Discoverability, not a missing feature.
2. **Per-line decline for reviewers.** Coordinators want to mark individual receipts as not accepted instead of rejecting the whole report. Today the only reviewer verdicts are report-level: endorse / reject, with a report-level `MaxAmount` cap.
3. **Structured intake for grant reports.** Free-text headers lose the information the team needs: the name the artist uses with the team (often not the Humans legal name), the project name, a contact email, and the granted amount. Preapprovals (shipped in peterdrier/Humans#1994) covers the amount and the program; the project name and the working name are not yet captured anywhere structured.
4. **Payees without a euro IBAN.** One artist outside SEPA was paid by a coordinator from personal funds via PayPal and reimbursed afterwards (Peter agreed by text). The team wants to know at grant time, not claim time, when a payee cannot be paid by SEPA transfer. Non-euro SEPA countries (Poland, Switzerland) are fine: the receiving bank converts, with a small fee.
5. **Deadlines and payment dates communicated up front.** Artists need the claim deadline early and want to know when money lands. Preapprovals carries `ClaimBy` and reminders; the intended payment cadence is a weekly SEPA run (submit → approve → pay Wednesday), blocked today by the bank dropping payee names from batch transfers (shows as "NCA").
6. **Approval workload.** Three coordinators are endorsing roughly 80 reports times their receipts. They want their wider team (the people who actually review the art) to be able to endorse, with amounts still set by coordinators. Endorsement today follows budget-category coordinators; Preapprovals adds per-program managers.
7. **Submitted reports not visible to some artists.** Unverified. The submitter can view their own report at any status; most likely they were not finding `/Expenses`. Needs a concrete case before it is treated as a bug.

## Decisions made in the conversation

- Non-SEPA payees are handled case by case for now; a flag at intake is wanted so it is known ahead of time.
- Weekly payment runs are the target once the bank's batch-transfer naming is sorted.
- Who may endorse will become configurable per department rather than fixed to the three coordinators.
