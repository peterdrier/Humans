# section-doctor — BoxOffice — 2026-11-21

- Invocation: --section=BoxOffice, interactive from a Claude project thread (Peter asked for a doctor run per new ticketing section)
- Anchor commit: `f642ca0f2` (origin/main at branch point); branch `section-doctor/2026-11-21T234918Z`.
- Budget: 2.5h.
- PR: pending

## Assessment summary

First doctor run on Box Office, the adapter that sells tickets through Stripe hosted Checkout behind Tickets' checkout port. The code held up: walking each flow against its invariants found no defect in what it charges, refunds or reports. What it lacked was proof. Half of the money-path guards (refund needs a settled payment, a Stripe refusal reads Failed, no answer throws so Tickets repeats it, cancelling a paid session refuses, the checkout's negative-line and zero-total refusals, the lifetime clamp) and the whole verified webhook path had no test. The invariant doc had drifted in small, checkable ways, and a few comments described an earlier version of the code.

- 2026-11-21 session: the SDK was preinstalled (10.0.404); builds and tests ran here, no toolchain workaround needed.
- 2026-11-21 session: no live render; the section has no page of its own (its admin row opens a Tickets page).
- 2026-11-21 session: nobodies-collective/Humans is not readable from this session, so upstream issues were not reviewed.
- 2026-11-21 session: InspectCode is not installed; that half of Prose & surface did not run.
- 2026-11-21 session: the branch is `section-doctor/<TS>` as this skill names it, not the session's default `claude/...` branch, so the selector's blocked set sees the run; the invocation (Peter asking for a doctor run) was read as asking for the skill's branch.

Independence check: pass. Findings 1, 2 and 9 came from the target: the invariant list in section 4 mapped against tests, and a shape mismatch (one conversion helper, three spellings).

Target diff: no previous target; this is the first.

## Findings

1. The money-path guards no test pinned: refund without a settled payment, a refund Stripe declines, a refund Stripe never answers, a failed refund Humans did not make, cancelling a paid session, a negative line or zero total, free lines left out, the session lifetime clamp.
2. The webhook had one test (a bad signature at the adapter); nothing pinned verified events naming sessions by object type, or the controller's 503, 400 and nudge.
3. `Docs/BoxOffice.md` said session expiry is clamped to Stripe's 30 minutes to 24 hours; the code clamps to 31 minutes .. 23 hours.
4. `Docs/BoxOffice.md` and the first target draft called the order id and refund id the idempotency keys; the keys are built from them (`humans_ticket_checkout_<order id>`, `humans_ticket_refund_<refund id>`).
5. `Docs/BoxOffice.md` described the webhook by event name and as 503 "while unset"; the code maps by the event's object and answers 503 when either variable is unset.
6. `Docs/BoxOffice.md` said code outside the project cannot name the adapter (its test projects can; no other section can), left out that a no-payment-required session reads Paid, lacked the template's Public contract and Migrated blocks, and had a doubled blank line.
7. Comments behind the code: `Section.cs` asked to rename "both places" when both read one constant; the controller summary gave the 503 for the secret only; the settings field carried a third copy of the key's scope list.
8. `Properties/AssemblyInfo.cs` opened internals to NSubstitute's proxy for internal interfaces the section does not have.
9. `BoxOfficeCheckoutBackend` converted Stripe times through `InstantOf` in the ledger and inline twice elsewhere.
10. `Docs/BoxOffice.md` triggers missed `TicketsBackendAdminNav.cs`, the sales index view and `memory/architecture/stripe-money-out.md`, all of which it makes claims about.
11. Off-section: `src/Sections/Humans.Tickets/Docs/features/checkout.md` says a dispute is read only as `RefundedMinor`; `DisputedMinor` exists and the Box Office, pretix and Hi.Events adapters fill it.
12. The webhook controller combines `IsWebhookConfigured` and `IsConfigured` itself.
13. For a delayed debit, `PaidAt` is the charge's creation time (when the buyer completed the page), not the day the money settled; Tickets dates sales and delivery by it.
14. The comment over the free-line filter in `CreateCheckoutAsync` half restates the filter.

## Debt verified

- The section has no debt ledger file and no rows in `docs/architecture/debt-ledger.yml`; nothing to verify. The cap was not reached.

## Worked

- 1, 2: tests added in `tests/Humans.BoxOffice.Tests/BoxOfficeCheckoutBackendTests.cs`; each failed against a mutated adapter (clamp at 30 minutes, `RefundFailed` without the Humans-id check, disputes naming no session, refund allowed without a paid charge) before the adapter was restored.
- 3, 4, 5, 6, 10: `Docs/BoxOffice.md` corrected; 4 also in the target.
- 7: comments rewritten.
- 8: `Properties/AssemblyInfo.cs` deleted; no test substitutes an internal type (`git grep -n "Substitute.For<"` in the section's tests finds only Stripe's public `IHttpClient`, and `Humans.Integration.Tests` names nothing in the section).
- 9: both call sites use `InstantOf`.
- 11: passed to the Tickets doctor run, which is open on that file at the same time; not written to Tickets' ledger to keep the two PRs from conflicting.
- Issue comments: none (no open section issues readable).

## Skipped

- 12: one expression shaping the response; a property for it adds a member to save none.
- 13: a question about what the date means, not a defect; see Needs Peter.
- 14: kept; "Stripe has no zero-amount line" is the why.
- reforge `surface-score` did not run; the section is small enough that the Shape thread read every member by hand.

## Retro

**Selector.** Chosen by Peter (`--section`), not the selector; a first run on a section built in one day, so the whole section was churn.

**Wasted motion.** Reading threads were dispatched for a section of a few hundred lines that main had already read whole; Freshness earned its keep (the template gaps and trigger misses), the others mostly confirmed what main had listed in their prompts.

**What striking revealed.** Every new test passed first time, so each was checked by mutating the adapter; without that a passing test of an untested guard proves nothing.

**Target diff.** First target. The draft repeated the section doc's idempotency-key wording, which Freshness then showed was loose: the target had copied the doc instead of the code.

## Needs Peter

- [ ] 13 — delayed-debit `PaidAt`: when the buyer completed the page (today) or when the money settled?

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.BoxOffice/Controllers/BoxOfficeStripeWebhookController.cs` | changed, reviewed |
| `src/Sections/Humans.BoxOffice/Docs/BoxOffice.md` | changed, reviewed |
| `src/Sections/Humans.BoxOffice/Docs/data-access.md` | reviewed |
| `src/Sections/Humans.BoxOffice/Docs/health.md` | changed (new) |
| `src/Sections/Humans.BoxOffice/Humans.BoxOffice.csproj` | reviewed |
| `src/Sections/Humans.BoxOffice/Properties/AssemblyInfo.cs` | changed (deleted) |
| `src/Sections/Humans.BoxOffice/Section.cs` | changed, reviewed |
| `src/Sections/Humans.BoxOffice/Services/BoxOfficeCheckoutBackend.cs` | changed, reviewed |
| `tests/Humans.BoxOffice.Tests/BoxOfficeCheckoutBackendTests.cs` | changed, reviewed |
| `tests/Humans.BoxOffice.Tests/Humans.BoxOffice.Tests.csproj` | reviewed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | opus 5.5 | 9, 12 |
| Behavior & bugs | main | opus 5.5 | 13 |
| Controller intent | subagent (`doctor-reader`) | opus low | 12 |
| Freshness | subagent (`doctor-reader`) | opus low | 3, 4, 5, 6, 10, 11 |
| Conformance | detectors on main, classified on main (self-run: no detector names this section) | opus 5.5 | none |
| Tests | subagent (`doctor-reader`) | opus low | 1, 2, 8 |
| Prose & surface | main (self-run: no resx, no views, InspectCode not installed; the admin row is reachable from the admin sidebar) | opus 5.5 | none |
| History | main (self-run: its whole input was a few lines, none narrating a prior state) | opus 5.5 | none |
| Comments | subagent (`doctor-reader`) | opus low | 5, 7, 8, 14 |
| Inbox | not dispatched: peterdrier/Humans covered with no open section issues; nobodies-collective/Humans not covered (probe of #1118 denied, repository not configured for this session); no ledger rows | none | none |
