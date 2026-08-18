# Section Doctor — Last Run Report

**Run:** 2026-08-18, Finance (first scheduled run; wrote the plan). Anchor `485a4714b`. Budget 2.5h.

## Assessment summary

Finance is structurally healthy and documentationally rotten. Every hard-rule check passes —
`Service` and `Repository` internal, one repository over every table the section holds, every cross-section
call through a contracts leaf, the Budget dependency already narrowed to `IBudgetServiceRead`,
no grandfathers, no obsoletes. The creditor-binding invariants are among the best-tested things
in the codebase: every write path is covered, including every concurrency window
nobodies-collective/Humans#995 named.

And the section doc still described the controller the G5 split deleted. `Finance.md` listed
Budget-CRUD routes `FinanceController` does not serve, a `POST /Finance/Creditors/Resync`
removed with the Holded v2 migration, an `ITicketServiceRead` cash-flow dependency the section
no longer has, and a Budget read-split filed as future work that had already shipped — while its
own Architecture section, further down the same file, described the split correctly. The doc
contradicted itself, and the half a reader hits first was the wrong half. Full scorecard:
`src/Sections/Humans.Finance/Docs/health.md`.

Nothing structural was taken, so the reforge score is unchanged at 254. That is deliberate: the
two findings behind it (an oversized service, a wide interface) are one problem — a
class that merged the doc pipeline with the creditor bindings — and unpicking it adds a type and
a DI registration, which is Peter's call.

## Worked

- **The section doc, rewritten against the code** (`e76f6af20`). The Budget half of `/Finance`
  now points at `Budget.md` instead of restating its route table — the duplication is what
  drifted. Cross-section dependencies rebuilt from the csproj: Budget (read-only), Holded, Users
  and Gdpr, of which only Budget was listed and Tickets was listed but absent. Inbound callers
  added. `HoldedFinanceService` → `Service`, `HoldedSyncState` → `HoldedDocSyncState`. Dead
  "all budget mutations in `FinanceController`" invariant and Budget-mutation trigger dropped.
- **The same claims swept out of four more files** — `FinanceController`'s own remark and
  `docs/sections/_Index.md` both said `BudgetAdminController` "stayed in Shell" when it is in the
  Budget section project; `Section.cs` and `IHoldedNightlySync` both put `HoldedSyncJob` under
  `Humans.Holded/Contracts/` when the HUM0034 carve-out moved it to `Jobs/`.
- **Two documented invariants had no test** (`3018d718a`). `MapDoc`'s Madrid conversion is the
  only place a Holded timestamp becomes a `LocalDate` and `GetMatchedForYearAsync` filters on
  `Date.Year`, so a whole budget year's actuals rest on it — a doc stamped 23:30Z on 31 December
  is already 1 January in Madrid. And the 2-minute contact-list cache the doc asserts as
  design-rules §15 Option A, read on every `/Finance/Creditors` and `/Expenses/{id}` load.
- **Two InspectCode findings** (`b97f5dc9f`): an unreachable null test on the non-nullable
  `TagsJson`, and `b` bound twice in the `Creditors` projection.

## Then Peter answered, in the same session

All six queued items came back the same day, so the run kept going rather than deferring to a
follow-up. Five of the six are applied in this PR:

- **`Service` split in two.** `HoldedDocService` (repo, client, `IBudgetServiceRead`, clock,
  logger) and `CreditorService` (repo, client, `IHoldedService`, clock, cache, logger). Every
  method body moved verbatim; the tests split the same way.
- **The public contract narrowed.** Each service is registered once and exposed twice — a
  contracts-leaf interface carrying only what other sections call, and a wider internal one only
  `FinanceController` resolves. Provisioning, the unmatched queue and bind/unbind stop crossing
  the assembly boundary. `IHoldedFinanceService` is gone. Reforge 254 → 215.
- **`RawPayload` dropped**, with the janitorial exception recorded in
  `no-drops-until-prod-verified` and the destructive-ops baseline appended by hand (the seeder
  overwrites other baselines' explanatory comments).
- **The rationale blocks trimmed** to the constraint plus a pointer to the `Finance.md` invariant
  that already carried the argument.
- **The rubric question closed** — no change; the score picks a section and is not expected to
  predict what will be wrong in it.

The sixth, the unread contract properties, came back as "need more info". Only
`HoldedMatchEntry.AccountNum` was unambiguous — internal, dead, deleted. The rest are per-property
judgment and are laid out on the PR, along with the larger finding behind them: `HoldedPaymentInfo`
is a *public* contracts type that never leaves `CreditorService`.

## Codex review

Two P2s, both real:

- **The method totals were wrong** — and the rule says not to write them at all.
  `no-derived-aggregates-in-docs` is a HARD RULE this run broke repeatedly: method totals, test
  counts, route counts, a section rank. Removed rather than corrected, per Peter. Worth noting how
  the error would have propagated: the queued narrowing was described as "down to nine methods"
  when ten have external callers, so following the note as written would have dropped a live one.
- **The cache test pinned the reuse but not the window.** Back-to-back calls pass against a cache
  with no expiry at all. `MemoryCacheOptions.Clock` takes an `ISystemClock`, so the suite now
  drives one by hand and asserts a second Holded call past the TTL — verified by removing the
  expiry from the service and watching the new assertion fail.

CI was also red on `dotnet format whitespace`, from the object initializer in the test helper this
run added.

## Retro

**What the plan/rubric got wrong.** Nothing yet — this run wrote the first plan. But it picked
Finance on score rank and never-served status, and the finding was doc rot, which neither signal
measures. That is the second run in a row where the ranking rubric was orthogonal to what was
actually broken (Guide, 2026-08-17, was the first). Two data points is a pattern worth naming:
the rubric is good at choosing *a* section and has predicted nothing about *what* will be wrong
in it.

**Wasted motion.** Two things. I wrote a `cat >` to `docs/health/plan.md` believing the shell
was in the repo root when it was in the worktree — it happened to be correct, but I only found
that out by checking afterwards, and the failure mode was writing to the main checkout. And I
grepped for view backlinks with an `asp-controller` pattern, concluded `CreditorStatement` was a
nav dead end, and had to walk it back when the file showed `asp-action="Creditors"` — the second
run in a row where a grep-shaped conclusion about a view did not survive reading the view.

**What the assessment missed that striking revealed.** The comment-volume finding. Five lanes'
worth of criteria and none of them asks "is the rationale in the right place" — I only saw it
from reading `Service.cs` end to end for the doc rewrite, and it is arguably the section's
largest single deviation from a standing project rule.

**Second-half retro.** The run broke a HARD RULE it had never read. `no-derived-aggregates-in-docs`
is in `memory/INDEX.md`, and I wrote counts into five files without checking whether a rule covered
them — the scorecard format itself invites it ("Tests | 57 in ..."), which is worth fixing in the
skill rather than in me. And the count that mattered was also *wrong*, which is the rule's own
argument: a hand-typed derived number drifts silently, and this one would have sent the queued
refactor at a method that has an external caller.

Two process notes. The destructive-ops baseline has a seeder, and running it clobbers unrelated
baselines' hand-written explanatory comments and reorders their entries — append by hand instead.
And `dotnet format whitespace` is a CI gate that a local `dotnet build` will not catch; it belongs
in the per-commit loop, not just before the PR.

