# Freshness sweep — 2026-08-25

**Mode:** diff
**Previous anchor:** `81080d53e`
**New anchor:** `78ee98869` (upstream/main)
**Worktree base:** `origin/main` @ `dd1a0d6a5` (frozen at start)
**Diff window:** 380 changed files, 15 commits

**Counts:** 8 of 9 mechanical entries dirty (`code-analysis-suppressions` clean).
83 editorial docs matched; 42 of them matched **only** sibling `.md` files and were
skipped as no-code-drift (see that section below), leaving 41 reviewed.
All 9 verifiers in `docs/scripts/freshness-checks/run-all.sh` pass.

## Updated automatically

- `reforge-history` — CSV rebuilt incrementally, 145 rows / 145 distinct days.
- `dev-stats` — 156 data rows; class/interface counts sourced from reforge for every
  new day (regex-fallback = 0 days).
- `about-page-packages` — Anthropic 12.40.0 → 12.42.0, Google.Apis.Auth 1.75.0 → 1.76.0.
  AwesomeAssertions and Meziantou.Analyzer bumps excluded (test/analyzer-only).
- `docs-readme-index` — added the Backdoor row to the section-invariants table.
- `authorization-inventory` — created `Humans.Backdoor/Docs/authorization.md`; documented
  the machine-request bypass in `MembershipRequiredFilter` / `NameRequiredFilter`; retired
  the `AgentApiController` / `FeedbackApiController` / `IssuesApiController` rows from
  Agent, Feedback and Issues (all deleted, consolidated into Backdoor).
- `dependency-graph` — added the missing `Backdoor --> User` edge
  (`BackdoorApiKeyService` → `IUserServiceRead`) and recounted the `linkStyle` indices,
  which were already stale at HEAD (comment said 279 eager, file had 281).
- `service-data-access-map` — 6 per-section maps regenerated plus the rollup; new
  `BackdoorDbContext` row (29 → 30 contexts); Events' `IUserDataContributor` note moved
  from `EventService` to `CachingEventService`.
- `guid-reservations` — verified against source, no change needed (no new seed GUIDs in
  the window; Backdoor seeds none).
- `AuditLog.md` `freshness:auto` block — added `FeedbackGitHubLinked`, `IssueCreated`, and
  the new Backdoor key-lifecycle actions.

## Fixed in place (editorial drift)

- **Surveys** — "Cross-section read interface: there is none" contradicted the doc's own
  Actors table; rewritten around `ISurveyAnalysisRead`. The "only public `Contracts/`
  member is `ISurveyReminderSender`" claim replaced with what is actually public there.
- **Auth / Onboarding** (4 docs) — `MembershipRequiredFilter` and `NameRequiredFilter`
  now pass Backdoor-API-key machine requests straight through; four docs stated their
  exempt lists as exhaustive, including an outright "Escape: none".
- **Feedback / Issues** — Contracts-leaf surface lists omitted `IFeedbackTriage`,
  `IIssueTriage`, `IssueStatus` and `IssueReadModels`.
- **design-rules.md** — Backdoor missing from the section list, and from the table-owning
  set. The cardinal counts that sat beside both lists were removed rather than incremented
  (Peter, `no-derived-aggregates-in-docs`).
- **Monitor.md** — reference set was missing `Humans.Users.Contracts`.
- **Debug** — `authorization.md` still listed the deleted `LogApiController` /
  `LogApiKeyAuthFilter`; `Debug.md` still explained `InMemoryLogSink`'s home by "Shell's
  `LogApiController` reads it" (it is Backdoor's `BackdoorLogsController` now);
  `authorization-inventory.md` still said `LogApiController` moved to Debug.
- **gdpr-export.md / Gdpr.md** — the Events contributor is `CachingEventService`
  delegating to `EventService`, not `EventService`. Contributor and referencing-project
  counts removed rather than incremented (same rule).
- **global-search.md** — stale `Section.cs` line citation.
- **Email.md** — carried over from the previous sweep's flag list, unactioned there:
  `IEmailService`'s callers were still counted as "nine `Humans.Application` services,
  six `Humans.Infrastructure` jobs". Both projects were deleted at G5; replaced with the
  named consuming section projects plus the Shell.

Verified against source and found accurate, no edit needed: `Backdoor.md`,
`admin-shell.md`, Debug's two feature docs, `code-review-rules.md`, `conventions.md`,
`roslyn-analysis.md`, `coding-rules.md`, `seed-data.md`, Events' and Agent's section and
feature docs (this window's Events changes are internal helper extraction, not behaviour),
`docs/guide/*`, `onboarding-pipeline.md`, `coordinator-roles.md`, `gate-terminal-login.md`,
`Cantina.md`.

## Dead trigger globs

- **Repaired:** none.
- **Unresolved:** none.

`verify-triggers.sh` reported `repaired=0 unresolved=0 docs_forced_dirty=0` both before
and after this sweep's catalog change.

## Editorial docs that matched only sibling `.md` changes

42 of the 83 matched editorial docs fired **only** because a sibling `.md` under
`src/Sections/Humans.X/**` changed, not because any code did. They were skipped rather
than reviewed — there was nothing to fix.

Attributed, they split three ways:

- **23 pure echo** — the only trigger was the *previous* sweep's own prose edit to a doc
  in that section. This damps: this sweep left those docs alone, so next sweep's window
  contains no `.md` change for them and they come back clean. One-sweep echo, not a
  standing loop.
- **19 mixed / 1 mechanical-only** — the trigger included a regenerated
  `Docs/data-access.md` or `Docs/authorization.md`, which change *because that section's
  source changed*. That is a legitimate (if indirect) drift signal, not noise.

Of the 69 `.md` changes under `src/Sections/*/Docs/` in this window, 55 came from the
previous sweep's own commit (`5a79da5fb1`), 9 from the Backdoor consolidation, 4 from the
no-tests-for-absences pass, 1 from the Events doctor run.

**No fix applied.** An earlier draft of this report called the pattern self-perpetuating;
that was wrong. The echo component is self-limiting and the rest is code-driven.

## Pruned

Three husks deleted (389 lines), all three explicitly deferred to this sweep by the
previous one on budget grounds. Wheat was extracted first — the previous sweep had
recorded them as "fully superseded, no wheat left", which was wrong for the account-merge
spec.

| Husk | Lines | Disposition |
|---|---|---|
| `docs/superpowers/specs/2026-06-06-account-merge-consolidation-design.md` | 121 | 3 wheat items migrated, rest chaff |
| `docs/superpowers/specs/2026-06-08-scanner-ticket-lookup-design.md` | 94 | 1 wheat item migrated, rest already in `Tickets.md` / `Scanner.md` |
| `docs/superpowers/specs/2026-06-09-team-early-entry-ticket-lookup-design.md` | 174 | 1 wheat item migrated, rest chaff |

**Wheat migrated**

- `2026-06-06 §Decisions/Target architecture` → `Humans.Users/Docs/Users.md` — a merge is
  atomic by *ordering*, not transaction: no cross-section `TransactionScope`, the tombstone
  is the single observable commit point, and every `IUserMerge.ReassignAsync` must stay
  idempotent so a partial merge is retryable. Verified against
  `AccountMergeService.MergeAsync` (steps 1–5, no scope) and `grep TransactionScope src`.
- `2026-06-06 §Reconciliation` → `Users.md` — merge-request state is derived from the
  tombstone; a Pending request whose two accounts already merged into each other is closed
  on sight, and one side merged into an unrelated third account is refused. Verified against
  `AccountMergeService.cs:265-277`.
- `2026-06-06 §What changes` → `Users.md` — no merge status beyond
  `Pending`/`Accepted`/`Rejected`; Dismiss reuses `Rejected`, auto-close uses `Accepted`,
  rows are never deleted. Verified against `AccountMergeRequestStatus.cs` and
  `CloseRequestsForPairAsync`.
- `2026-06-08 §Context` → `Humans.Tickets/Docs/Tickets.md` — Ticket Tailor issues two
  identities per ticket and has **no** lookup-by-barcode endpoint, so every barcode
  resolution runs against synced rows and the cached projection. Verified against
  `ScannerController` / `TicketAttendee.Barcode`.
- `2026-06-09 §1 Shared picker` → `docs/architecture/conventions.md` — extra result sources
  join `<vc:human-search>` as opt-in URL attributes, never by widening the Users-owned
  `/api/profiles/search`; each fetch degrades to `[]` independently; a no-result query is
  silent. Verified against `HumanSearchPickerViewModel.TicketLookupUrl` and
  `TeamAdminController.LookupTicket`.

**Inbound refs retargeted**

- `Humans.Users/Docs/features/profile-search-detail.md:115` — pointed at the deleted
  early-entry spec; now points at the `conventions.md` rule.

**Also fixed in the sweep's own machinery**

- `docs/architecture/freshness-catalog.yml` — `src/Sections/*/Docs/authorization.md` added
  to `ignore:`. It is a mechanical output of the `authorization-inventory` entry, exactly
  like the already-ignored `data-access.md`; without it all ~40 of them landed in the
  "Unmarked editorial" flag list every sweep, asking for markers they must never carry.
  Cuts that list from 45 docs to 5.

**Not pruned, deliberately**

- `2026-06-14-rideshare-section-design.md` (271) — explicitly a *future* spec ("Design only
  — not scheduled for build. Targeted for Q4"). Not historical; excluded again.
- `docs/architecture/tech-debt-2026-04-23.md` (227) — the allowlist admits it only when
  every item is `[DONE]`, and several are still open. `debt-ledger.yml:356` tracks it.
- Everything in `docs/plans/` (5 files, newest `2026-08-07`) and
  `docs/superpowers/plans/` (2 files) is under the 30-day age bar.

Deleted 389 lines against a 28,229-line corpus — 1.4%, below the 5% soft target. Nothing
else was eligible under the age rule; this is a shortage of eligible husks, not a
deferral.

## Unmarked editorial (add `freshness:triggers`)

5 docs, all Surveys feature specs:

- `Humans.Surveys/Docs/features/grid-questions.md`
- `Humans.Surveys/Docs/features/survey-information-blocks.md`
- `Humans.Surveys/Docs/features/survey-intro-markdown.md`
- `Humans.Surveys/Docs/features/survey-invitation-email-copy.md`
- `Humans.Surveys/Docs/features/survey-preview.md`

## Flagged for human review

None. Every concrete broken fact found this sweep was fixed in place.

## Proposed for review

None — all prune candidates resolved this sweep.

## Questions

Raised with Peter inline at the end of this run:

1. Editorial docs matching only sibling `.md` changes — **resolved, no fix needed.**
   Peter: wouldn't they stop dirtying once there are no further changes? Correct; measured
   above (23 of 42 are a one-sweep echo that damps, the other 19 are code-driven).
2. Stale `TransactionScope` comments — **fixed.** `RoleAssignmentService.cs:271` and
   `CampService.cs:1392` now point at `AccountMergeService.MergeAsync` and its ordered,
   idempotent fan-out.
3. Counts in docs — **fixed, and generalised.** `CLAUDE.md` now says "all of them".
   Peter's rule ("we don't allow counts in docs") already existed as
   `no-derived-aggregates-in-docs`; extended that atom to cover counts of a code-owned set
   with no list in the doc, and stripped the counts this sweep had refreshed in
   `design-rules.md`, `gdpr-export.md` and `Gdpr.md`.
4. The `authorization.md` ignore-list addition — **approved by Peter for now.**

## Skipped (errors)

None.
