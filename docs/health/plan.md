# Section Doctor — Plan

**Anchor:** `485a4714b` (origin/main, 2026-08-18). Scores from
`reforge surface-score --format compact` on a built solution at that commit.

**Rubric this cycle:** first cycle, so nothing but Containers and Guide has ever been
doctored. Peter's steer (2026-08-18) is to work the **middle of the pack** rather than the
biggest section first — Users at 2822 is five times the next section and would swallow many
runs before showing a return, while the mid band is small enough that a single run can
actually finish a section. Within the mid band, never-served-by-any-refactor-lane wins
(from the Section Refactor History table), then score descending. Sections with in-flight
or planned feature work are skipped.

**Skipped and why:** Users (2822), Shifts (1081), GoogleIntegration (1014), Teams (777),
Tickets (580) — top of the pack, deferred per the steer. Containers, Guide — already
doctored. Store (220) — in-flight feature work (nobodies-collective/Humans#1029 Store
Phase 5 Holded v2 invoicing, #158). Web/Interfaces/Analyzers/Development — not sections.

| Date | Section | Score | Why now |
|---|---|---|---|
| [x] 2026-08-18 | Finance | 254 | Never served by any lane; reshaped by the Holded v2 split (#1306) and never re-examined; the live domain in the current post-event finance/cleanup phase |
| [ ] 2026-08-19 | Expenses | 273 | Top of the mid band; last lane 2026-05-30 (#830), the longest-stale of the mid band |
| [ ] 2026-08-20 | Budget | 257 | Last lane 2026-05-30 (#836); pairs with Expenses — same money domain, adjacent findings |
| [ ] 2026-08-21 | Camps | 247 | Last lane 2026-05-29 (#822) |
| [ ] 2026-08-22 | Mailer | 225 | Never served by any lane; absent from the Section Refactor History table entirely |
| [ ] 2026-08-23 | Events | 173 | Last lane 2026-06-11 (#967) — most recently served of the band, so last |
| [ ] 2026-08-24 | Notifications | 166 | Last lane 2026-06-01 (#852) |

## Needs Peter

Unticked items are open. `resume` works this list for merged runs, and the PR body for runs whose
PR is still open.

### 2026-08-18 — Finance (peterdrier/Humans#PENDING)

- [ ] **Split `Service` (856 lines) along the doc-pipeline / creditor-bindings seam?** They share
      no state and no invariant — one is a nightly full-pull with attribution and an unmatched
      queue, the other a member↔account link with a three-way concurrency story. The split retires
      both of the section's reforge findings and is behaviour-preserving, but it adds a type and a
      DI registration.
- [ ] **Take the five admin-only methods off the public `IHoldedFinanceService`?**
      `GetProvisioningPlanAsync`, `ProvisionAsync`, `GetUnmatchedAsync`, `SetCreditorContactAsync`
      and `ClearCreditorContactAsync` have no caller outside this project — they cross an assembly
      boundary for `FinanceController`'s benefit alone. The contract Budget, Expenses and Holded
      consume is nine methods. Landing them on an internal interface is surface *addition*.
- [ ] **Drop `RawPayload`?** A NOT NULL jsonb column on `holded_expense_docs` that has only ever
      held the literal `{}` — `MapDoc` never wrote a payload and nothing reads it. Schema change.
- [ ] **Trim `Service.cs`'s rationale blocks to 1–3 lines?** ~15 comment blocks, several 10–14
      lines with decision history and issue archaeology inline, against `comments-stay-short`.
      Every one is accurate and most restate a `Finance.md` invariant. ~200 lines; the judgment is
      whether the doc is genuinely the right home for all of it.
- [ ] **Six contract properties InspectCode reports as never read** —
      `HoldedCreditorStatus.SupplierAccountNum`, `HoldedPaymentInfo.DocumentType`,
      `HoldedUnmatchedRow.HoldedDocId`, `CreditorContactBinding.HoldedContactId`,
      `CreditorLedgerLine.AccountNum`, `HoldedMatchEntry.AccountNum`. Left alone: on a contract
      record "nobody reads it today" is weak evidence, and two are the natural key of their row.
      Delete, or is carrying them correct?
- [ ] **Second data point on the rubric question already open from Guide.** Guide (score 8) was
      failing open on access control; Finance (254) had a section doc two versions behind. Neither
      is something score growth or staleness can see. The rubric picks *a* section fine and has
      predicted nothing about *what* is wrong. Worth changing, or is picking a section all it is for?

### 2026-08-17 — Guide (peterdrier/Humans#1354)

- [ ] **`GuideFiles.TryCanonical` shipped without the second-opinion gate.** Four attempts across
      three reviewer agents all went idle without a verdict; I worked their checklist myself (one
      concept; behaviour identical on null/empty/whitespace; no seam lost, all ten call sites
      passed the same set) but that is self-review. Judge it, or say revert — nothing depends on it.
- [ ] **The reviewer gate could not be obtained at all this run.** Should Phase 4.4 keep a
      subagent reviewer, or move to a fallback the main thread runs and labels as self-review?
- [ ] **Filter markdown instead of HTML?** The structural cure for Guide's fail-open class: cache
      markdown, drop blocks the viewer cannot see, then render. Removes `GuideFilter`'s regex and
      the `data-guide-*` attributes. Blocked only by the cache holding rendered HTML, which at 28
      files and ~500 users is not a real constraint. Worth an issue?
- [ ] **Should `NotFound` / `Unavailable` render the sidebar?** They reserve its column and leave
      it empty, so a mistyped guide URL strands the reader with one link home.
- [ ] **Is a low reforge score being read as "healthy"?** Guide scores 8 — lowest of any section —
      and was serving an admin block to anonymous visitors. The replan rubric ranks by score growth
      and staleness and would never have scheduled it. Should the rubric change?
