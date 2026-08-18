# Section Doctor — Last Run Report

**Run:** 2026-08-18, Finance (first scheduled run; wrote the plan). Anchor `485a4714b`. Budget 2.5h.

## Assessment summary

Finance is structurally healthy and documentationally rotten. Every hard-rule check passes —
`Service` and `Repository` internal, one repository over all four tables, every cross-section
call through a contracts leaf, the Budget dependency already narrowed to `IBudgetServiceRead`,
no grandfathers, no obsoletes. The creditor-binding invariants are among the best-tested things
in the codebase: 31 tests across three write paths, including every concurrency window
nobodies-collective/Humans#995 named.

And the section doc still described the controller the G5 split deleted. `Finance.md` listed 23
Budget-CRUD routes `FinanceController` does not serve, a `POST /Finance/Creditors/Resync`
removed with the Holded v2 migration, an `ITicketServiceRead` cash-flow dependency the section
no longer has, and a Budget read-split filed as future work that had already shipped — while its
own Architecture section, further down the same file, described the split correctly. The doc
contradicted itself, and the half a reader hits first was the wrong half. Full scorecard:
`src/Sections/Humans.Finance/Docs/health.md`.

Nothing structural was taken, so the reforge score is unchanged at 254. That is deliberate: the
two findings behind it (an 856-line service, a fourteen-method interface) are one problem — a
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

## Skipped and why

- **Splitting `Service`** along the doc-pipeline / creditor-bindings seam — the ideal-shape move,
  and the cure for both reforge findings. Adds a type and a DI registration → Needs Peter.
- **Narrowing `IHoldedFinanceService`** from fourteen methods to the nine with external callers.
  The five admin-only ones want an internal interface → surface addition → Needs Peter.
- **Dropping `RawPayload`**, a NOT NULL jsonb column that has only ever held `{}` → schema change.
- **Trimming `Service.cs`'s rationale blocks.** ~200 lines of accurate but 10–14-line comment
  essays, mostly restating `Finance.md` invariants → Needs Peter.
- **Six unread contract properties** InspectCode flagged. Deliberately left: on a contract record
  "no consumer reads it today" is weak evidence, and two of them are the natural key of their row.
- **Stryker** — not run. The tests lane was not dispatched this run (see retro).

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
