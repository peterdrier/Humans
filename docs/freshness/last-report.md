# Freshness sweep: 2026-10-21

**Mode:** diff
**Previous anchor:** `25a06a1c4`
**New anchor:** `cb3d8b37` (upstream/main)
**Worktree base:** `origin/main` @ `5eddd89c` (frozen at start; upstream/main is an ancestor)
**Diff window:** 680 changed files, 12 non-merge commits

**Result:** every mechanical entry was dirty except `code-analysis-suppressions`. Every dirty
editorial doc was reviewed. All verifiers in `run-all.sh` and `diff-mode.sh` pass, and
`verify-triggers.sh --check` finds no dead triggers after repair.

Docs were checked against current source, which includes the Users decomposition (#2055) and the
controller-intent work that landed on origin/main after the anchor. Those changes drove most fixes.

## Updated automatically

- `reforge-history`: 2 rows appended (2026-10-20, 2026-10-21).
- `dev-stats`: 2 rows appended. Class/interface counts come from reforge for both days.
- `authorization-inventory`: `MyDataController` added to the Consent map; drifted `AuthorizeAsync`
  and view-map line pins refreshed; TeamResources and CityPlanning call sites added.
- `dependency-graph`: edges rebuilt from source. New nodes `HoldedDocumentSync`, `SepaBankBooking`,
  `EmailProblemsService`, `AgentUserSnapshotProvider`, `AgentToolDispatcher`. Stale edges from before
  the Users decomposition removed.
- `service-data-access-map`: `ExpenseHoldedBookingService` heading added; constructor-vs-doc drift
  fixed in 8 more section maps; rollup drops retired invalidators and cache keys, adds the new tables
  (consent reviews, membership tiers, workspace identities, payment details).
- `docs-readme-index`: Backdoor `finance-read-api` and Tickets `accountant-donor-list` indexed.
- `about-page-packages`, `guid-reservations`: checked against source, no change.

## Fixed in place (editorial drift)

- **Architecture:** `design-rules.md` replaces deleted `IUserInfoInvalidator` examples and marks
  invalidators as HUM0028 legacy (§15e/§15f); §8c Art. 9 write path is
  `IUserManagement.SaveMedicalConditionsAsync`; Cantina is off the tableless list; §11 handler table
  gets `TeamInfo`, `TeamResourceAuthorizationHandler`, `AgentRateLimitHandler` and
  `CityPlanningMapAdminHandler`. `code-review-rules.md` no longer expects new cross-section
  `I*InfoInvalidator` signals.
- **People:** guides Onboarding, SigningIn, LegalAndConsent, YourData and Profiles follow the tier card
  (links to the Governance application), `/OnboardingWidget/Names`, and the "My Data" page at
  `/Consent/MyData`. `Onboarding.md`, `volunteer-status.md`, `onboarding-pipeline.md`, `Users.md`,
  `profiles.md`, `preferred-email.md`, `contact-fields.md`, `email-flag-violations-remediation.md`,
  `profile-pictures-birthdays.md` and `Auth.md` follow the Users decomposition moves (deletion and
  data export in Gdpr, consent review in Consent, workspace identity status in GoogleIntegration,
  `VerifyLoginTokenAsync` returns bool, `RoleAssignmentsController` owns the role policy).
- **Money:** `Expenses.md` (IBAN now in Finance payment details), `Finance.md`, `Holded-connector.md`
  (two deletes), `Budget-feature.md` (Stripe fee VAT rows), `Workgroups.md` and `workgroup-budget.md`
  (tiers from `IGovernanceService`), `preapprovals.md` (`PreapprovalsAccess` policy).
- **Tickets/Store:** guides Tickets (Net Revenue shows fee VAT and withheld tax) and Store (Issue
  invoice stays active with a notice); `Tickets.md`, `ticket-vendor-integration.md`, `Store.md`,
  `Store-feature.md` (zero balance only for new issuance; re-issue refused without a Holded call).
- **Governance/Shifts/Teams/Camps:** guides Shifts, Teams, Governance; `Governance.md`,
  `asociado-applications.md`, `membership-tiers.md` (no inline application), `Shifts.md` (participations
  are Tickets'), `coordinator-roles.md` (consent review does not gate admission),
  `shift-preference-wizard.md`, `47-volunteer-tracking.md`, `Camps.md` (entered-event = Attended),
  `Teams.md`, `Teams-feature.md` (leave diagram: Google access removed by daily reconciliation).
- **Events/Agent:** guide Calendar (feed URL made on button press), `Agent.md` snapshot dependencies.
- **Other:** `background-jobs.md` (Holded expense outbox every 5 min), guide Issues (active assignees
  only), `AuditLog.md` (two new audit actions, Containers writer, removed method), `Campaigns.md`
  (`IUnsubscribeService` is Email's), `Monitor.md`, `Debug.md`, `daily-roster.md`
  (`DietaryProfileInfo`), `43-google-group-membership-sync.md`, `google-integration.md` (Shared Drive
  root name from `drives.get`).

## Dead trigger globs

### Repaired
- `docs/guide/LegalAndConsent.md`, `docs/guide/YourData.md`: `Humans.Users/Views/Profile/Privacy.cshtml`
  (deleted by #2055) now points at `Humans.Users/Views/Profile/Index.cshtml` and
  `Humans.Gdpr/Views/User/**`. The script's basename match picked the site privacy policy
  (`Views/Home/Privacy.cshtml`), which is a different page, so the repair was corrected by hand.

### Unresolved
- None.

## Pruned

Over the 5% target after Peter's answer on the G5 citations; the overage is the one large spec
whose source citations he called relics.

- `docs/superpowers/specs/2026-07-15-per-section-dbcontext-design.md`: the peel programme is
  done; durable rules moved to design-rules §2b.
- `docs/superpowers/specs/2026-08-14-g5-endstate-design.md`: already in design-rules §1 except
  one rule, migrated.
- `docs/superpowers/specs/2026-08-14-g5-endstate-inventory-application.md` and
  `...-inventory-infra-ui.md`: dated inventories of deleted projects, all chaff.
- `docs/superpowers/specs/2026-08-07-g5-section-project-split-design.md`: its durable content was
  already in `G5-SECTION-TEMPLATE.md` and design-rules §1/§2b/§8c; the "design §N" comments that
  cited it were stripped from source.

### Wheat migrated
- per-section-dbcontext §4, §12 → design-rules §2b: baselines are fake-applied only with empty history
  and a present sentinel; sentinels are matched by name; no migrate-down below a baseline
  (`SectionMigrationRunner`).
- per-section-dbcontext §5.1, §10.3 → §2b: physical schema must match the model
  (`PhysicalDefaultParityTests`).
- per-section-dbcontext §8 → §2b: `SystemDbContext` holds framework tables only.
- g5-endstate §Base floor → design-rules §1: analyzers derive marker-attribute names with
  `typeof(X).FullName` (`Humans.Analyzers.csproj` linked sources).

### Retargeted refs
- `memory/process/ef-multi-context-commands.md` → design-rules §8.
- `2026-08-07-g5-section-project-split-design.md` (3 refs) rewritten as historical.

### Not pruned
- Too young: `2026-10-19-users-decomposition-plan.md`, `docs/superpowers/plans/2026-09-25-workgroup-budget.md`
  (ages out 2026-10-25). Kept on purpose: the freshness-sweep, debt-sweep and section-doctor designs
  (cited from `.claude/` skills), the Pleo draft, and `fk-cut-inventory.md` (cited by code).

## Unmarked editorial (add `freshness:triggers`)

- `Humans.Finance/Docs/features/sepa-payout.md`, `Humans.Notifications/Docs/features/notification-api.md`,
  `Humans.Backdoor/Docs/features/finance-read-api.md`, and six Surveys feature docs: grid-questions,
  ranked-choice-voting, survey-information-blocks, survey-intro-markdown, survey-invitation-email-copy,
  survey-preview.

## Flagged for human review

- `coordinator-roles.md` still calls the consent check a "Volunteer-level safety gate" although it does
  not gate admission (wording only).

## Proposed for review

None. All candidates resolved this sweep.

## Questions

Asked inline in the sweep's thread; Peter's answers, applied in this PR:
1. Rejection decision note: docs say required, code accepted an empty one. **Fix the code.**
   `ApplicationDecisionService.RejectAsync` now refuses a blank note (`DecisionNoteRequired`), and the
   Board voting page shows a localized error.
2. `membership-tiers.md` US-15.4 "Admin tier downgrade": tiers end only at term expiry. Story dropped;
   rule 7 now says downgrades happen only through `MembershipTierExpiryJob`.
3. Expenses guide "IBAN on your profile": the wording describes where the member sees it in the app,
   not where the data lives. **No change.**
4. G5 split design citations in source: historical relics. Comments stripped and the spec deleted.
5. AGENTS.md section count: we don't count in docs. Number removed, and
   `memory/process/no-derived-aggregates-in-docs.md` now says any count found is removed on sight.
   `peters-hard-rules.md` still says "~40 small apps"; that file is Peter's to edit.

## Skipped (errors)

- None. Note: `reforge-history` and `dev-stats` read the local `main` ref; it was stale in this
  container, so both were re-run after pointing it at `origin/main`.
