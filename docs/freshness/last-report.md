# Freshness sweep: 2026-11-08

**Mode:** diff
**Previous anchor:** `8eacc6bf`
**New anchor:** `48eb2e5b` (upstream/main)
**Worktree base:** `origin/main` @ `9fc2ddc9` (frozen at start; contains upstream/main)
**Diff window:** 28 commits, 1,194 files. Most were UI-component churn (page-header, `@section Crumbs`, sortable
table, status-badge, empty-state, JS helpers) and the nl culture. The behavior changes were the tech-debt sweep's
service extractions and migration retirements, the Stripe mirror, Pleo booking into Holded, the Feedback section
removal, the Board roster read, `/Debug/Database`, and two dropped settings columns. That left 154 marked
editorial docs and every mechanical entry dirty. Each dirty doc was checked against the substantive commits.

**Result:** 24 entries updated (4 mechanical, 20 editorial docs). Every verifier in
`docs/scripts/freshness-checks/` passes except `dev-stats` (environmental, see Skipped). `diff-mode.sh` passes
17/17, and `verify-triggers.sh --check` finds no dead triggers.

## Updated automatically

- `dependency-graph`: redrawn from every service constructor. It adds the services extracted by the debt sweep
  (Shifts readers/metrics, Users readers/mutations, Governance notices/effects and others), which now carry the
  lazy edges `ShiftManagementService` used to hold. 8 services joined the no-edge roster and ShiftViewService
  left it. `linkStyle` was re-indexed.
- `service-data-access-map`: 31 extracted services got headings across 13 section maps. Finance `Service` and
  `SepaBankBooking` now go through `IHoldedService`/`IHoldedWrite` and the TrackedCache slot. All four Shifts
  dashboard cache keys are listed, and Store names its three helpers. In the rollup, the Feedback line is gone and
  the AdminDatabaseDiagnostics entry covers `/Debug/Database`.
- `authorization-inventory`: added the UsersController row in Users `authorization.md`. The Teams Roster row now
  names `ResolveTeamViewAsync`.
- `reforge-history`: 5 days appended.
- `about-page-packages`, `docs-readme-index`, `guid-reservations`, `code-analysis-suppressions`: checked, no change.

## Dead trigger globs

### Repaired
None.

### Unresolved
None.

## Fixed in place (editorial drift)

- **Money:** `admin-shell.md` (Stripe sidebar group, Finance "Pleo reconcile" tab); `sepa-payout.md` (a retry
  after PARTIAL resumes from saved payment ids); `Holded.md` (seven owned tables, contact/bank mirrors, full
  inbound caller list: Expenses, Store, Workgroups, Backdoor); `workgroup-budget.md` (expense-account link uses
  the chart mirror); `Budget.md` (Feedback dropped from the decorator rationale).
- **People:** `Teams-feature.md`, `Teams.md`, `hidden-teams.md`, guide Teams/Profiles (Roster through the
  team-view resolver; birthdays/map now at `/Users/Birthdays`, `/Users/Map`); `Users.md` (Workspace-identity
  columns dropped, `EnsurePrimaryInvariantAsync` on UserEmailMutations); `notification-inbox.md` (FeedbackResponse
  purged as retired); `google-removal-notifications.md`; `Shifts.md` (ShiftStaffingAggregation);
  `Governance.md` (ApplicationReader).
- **Platform:** guide Admin, `administration.md`, `Debug.md` (`/Debug/Database`); `CityPlanning.md`,
  `Surveys.md` (Feedback dropped from the decorator rationale).

## Pruned

Nothing this sweep. The remaining candidates are all cited: `fk-cut-inventory.md` is a test baseline; the
freshness-sweep, debt-sweep and section-doctor designs are referenced by `.claude/` skills; and
`users-decomposition-plan.md` (2026-10-19) is younger than 60 days.

## Unmarked editorial (add `freshness:triggers`)

Surveys `ranked-choice-voting.md`, `grid-questions.md`, `survey-preview.md`, `survey-information-blocks.md`,
`survey-intro-markdown.md`, `survey-invitation-email-copy.md`; Backdoor `finance-read-api.md`; Finance
`sepa-payout.md`; Notifications `notification-api.md`; Stripe `stripe-mirror.md`.

## Flagged for human review

- `.claude/skills/test-site/SKILL.md` smoke steps still hit `/Teams/Map` and `/Teams/Birthdays`, which moved to
  `/Users/*` with no redirect. This is outside the sweep's scope.
- `tests/e2e/tests/admin-shell.spec.ts` sidebar matrix has no Stripe, Pleo or "Pleo reconcile" rows (test code).
- `src/Sections/Humans.Budget/Docs/health.md` still cites Feedback. It is a doctor-owned file, so it was left.
- These are kept as snapshots: `notification-board.md`, the dated Surveys/Workgroups design records (no nl, Feedback
  references) and the historical problem statement in `user-search-overhaul.md`.

## Proposed for review

None — all candidates resolved this sweep.

## Questions

- `Users.md`: should the "Frozen legacy (awaiting drop)" table list `users.GoogleEmail`? The prose says it is
  still a shadow column, and it was retired by nobodies-collective/Humans#1102 rather than by the
  `google.workspace-identities` migration.

## Skipped (errors)

- `dev-stats`: `cloc` is not installed in this environment and installing it was refused. Re-run locally.
