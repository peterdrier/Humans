# Freshness sweep: 2026-11-05

**Mode:** diff
**Previous anchor:** `f71de1eb`
**New anchor:** `8eacc6bf` (upstream/main)
**Worktree base:** `origin/main` @ `3e48ed48` (frozen at start; contains upstream/main)
**Diff window:** 67 commits, 1,933 files. The Users-core and User-entity moves into Base touched nearly every
section, so every mechanical entry and 159 marked editorial docs were dirty. Matched files were triaged for real
behavior change; pure namespace churn was skipped.

**Result:** every dirty doc was reviewed and edited where it had drifted. Every verifier in
`docs/scripts/freshness-checks/` passes except `dev-stats` (environmental, see Skipped); `diff-mode.sh` passes
17/17 and `verify-triggers.sh --check` finds no dead triggers.

Most drift came from: the Users core, audit append path and GDPR contributor seam moving into `Humans.Base`
(sections lost their AuditLog/Gdpr project refs); the Feedback section's removal; the Holded/Finance/Pleo
reshuffle (`IFinanceService`, purchase mirror in Holded, `/Finance` overview, Budget admin at `/Budget/Admin`,
Pleo mirror); Board read-only access (#2219); and Users' legacy storage drop (null rows now read as historical).

## Updated automatically

- `dependency-graph`: added ConsentReviewQueueService, FinanceOverviewService, HoldedWrite, PleoService,
  ShiftSignupNotices, SurveyTranslationFiller; Finance/Store nodes renamed FinanceService/StoreService; removed
  HoldedDocumentSync; ~20 stale edges dropped (UserEmailService, ShiftView, PaymentDetails), AuditViewer/HoldedWrite
  edges added; roster gains TeamGoogleGroupMemberships, UserSignInService, ShiftViewService; `linkStyle` re-indexed.
- `service-data-access-map`: ShiftSignupNotices, SurveyTranslationFiller, RideshareNotices headings; rollup
  (Users legacy tables gone, HoldedPurchaseDocs, GrantsDbContext row, 36 section contexts); dependency lists in
  Shifts, Surveys, Rideshare, Finance, Consent, CityPlanning, Issues, Tickets, Users, Governance, Camps, Google.
- `authorization-inventory`: `AuthorizeAsync` call-site table and view-map line numbers regenerated (Board Read
  checks on Shifts/Teams, Grants/Preapprovals Read/Manage split, PrivateUploadMiddleware row removed). No
  per-section `authorization.md` needed changes.
- `about-page-packages`: Ical.Net 5.2.4, Google.Apis.Auth 1.78.0, AngleSharp 1.9.0, HtmlSanitizer 10.0.1120,
  Markdig 1.5.1.
- `docs-readme-index`: dead `superpowers/plans/` row removed, `plans/` row narrowed to what remains, stale
  four-layer architecture blurb replaced with the sections/Base/Shell shape.
- `reforge-history`: 3 days appended.
- `guid-reservations`, `code-analysis-suppressions`: checked, no change.

## Dead trigger globs

### Repaired
- `Rideshare.md` — `src/Sections/Humans.AuditLog/Contracts/IAuditLogService.cs` → `src/Humans.Base/AuditLog/IAuditLogService.cs`
- `Rideshare.md` — `src/Sections/Humans.Gdpr/Contracts/IUserDataContributor.cs` → `src/Humans.Base/Gdpr/IUserDataContributor.cs`

### Unresolved
None.

## Fixed in place (editorial drift)

- **Architecture/global:** `AGENTS.md` (Base has no project refs; holds Users core, audit append, GDPR seam);
  `design-rules.md`, `code-analysis.md`, `roslyn-analysis.md` (User entity and audit write face in Base);
  `conventions.md` (Feedback mentions); `background-jobs.md` (FeedbackReplyDelivery gone; legal-doc sync sends
  in-app notices plus one email per member); `admin-shell.md` (Pleo group, real Finance/Holded items);
  `seed-data.md` (Pleo category seeds); `Backdoor.md`, `finance-read-api.md` (feedback API gone, expense-recon
  route); `Development.md`; `Feedback.md` tombstone date.
- **Users/Auth:** `Users.md` (AccountMarkedFake, IUserWrite members, IUserEmailService, contributor names);
  `Auth.md` (IUserSignInService); `authentication.md`; `user-search-overhaul.md` (State-based rejection);
  `email-flag-violations-remediation.md`; guide YourData.
- **Consent/Onboarding/Governance:** `Consent.md` (review queue owned here, every refusal shows a message);
  `legal-documents-consent.md` and `volunteer-status.md` (grace expiry suspends via
  SuspendNonCompliantMembersJob); `Governance.md`, `assembly-votes.md`; `onboarding-pipeline.md`; guide
  LegalAndConsent.
- **Money:** `Finance.md`, `Budget-feature.md`, guide Budget (`/Budget/Admin`, `/Finance` overview, IsActive
  dropped); `sepa-payout.md`, `expense-reimbursement-process.md` (SEPA review step; refusals table repaired);
  `Holded-connector.md`; `Expenses.md`; `Store.md`, `Store-feature.md`, guide Store (one checkout attempt per
  order, invoice-recovery refusal); `grants.md`; `preapprovals.md`; `workgroup-budget.md`; guide Expenses (IBAN on
  the report page); `Pleo.md` (migrated wheat).
- **Tickets/Gate:** `Tickets.md`, `event-participation.md`, Tickets `data-access.md` (legacy fallback gone);
  `gate-terminal-login.md` (account marked fake); `Gate.md`, `Containers.md`, `EarlyEntry.md`; guide Tickets.
- **Events/Camps:** `Camps.md`, `Cantina.md`, `daily-roster.md` (one audit row per reveal), `CityPlanning.md`,
  `Events.md`, `Rideshare.md` (history prose → current state).
- **Shifts/Teams/Google:** `Teams.md`, `Teams-feature.md`, guides Teams/Shifts/GoogleIntegration (Board
  read-only); `GoogleIntegration.md`, `workspace-account-provisioning.md`, `google-integration.md`,
  `43-google-group-membership-sync.md` (per-group transport errors, Drive retry); `google-removal-notifications.md`
  (nl); `Shifts.md`, `email-a-rota.md`; `Workgroups.md` (MeetingNotes log kind).
- **Email/Notifications/platform:** `Email.md`, `communication-preferences.md`; `Issues.md`, `issues-system.md`,
  `Agent.md`, `Agent-feature.md`, guide Issues (Feedback gone); `Campaigns.md`, `MailerLite.md`, `Monitor.md`,
  `Settings.md`; guide Admin (bell semantics); Surveys `ranked-choice-voting.md`, `survey-preview.md`.

## Pruned

- `docs/superpowers/specs/2026-08-10-pleo-connector-design.md` (52 lines). Pleo shipped (#2276); `Pleo.md`
  already covers the API, tables, sweep and pages.

### Wheat migrated
- Pleo spec §Pleo API facts → `Pleo.md` Invariants: the mirror polls (no entry-level webhooks); cardholders via
  `/v2/employees`. Verified against `PleoClient` (`/v2/employees`, `accounting-entries:search`).

### Not pruned
Under the 5% target (2,178 lines): the remaining candidates are cited. `fk-cut-inventory.md` (test baseline), the
freshness-sweep, debt-sweep and section-doctor designs (`.claude/` skills); `users-decomposition-plan.md` is too
young.

## Unmarked editorial (add `freshness:triggers`)

Surveys `ranked-choice-voting.md`, `grid-questions.md`, `survey-preview.md`, `survey-information-blocks.md`,
`survey-intro-markdown.md`, `survey-invitation-email-copy.md`; Backdoor `finance-read-api.md`; Finance
`sepa-payout.md`; Notifications `notification-api.md`. (Checked this sweep for this window's concrete drift.)

## Flagged for human review

- **Code bug, not doc drift:** `/Teams/{slug}/Roster` is `[Authorize(BoardOrAdmin)]` but calls
  `ResolveTeamManagementAsync`, which `TeamAuthorizationHandler` denies to Board → Board gets 403. Docs still say
  Board can open it (Teams.md, guide Teams, Teams-feature US-6.13); left until the code is decided.
- `tests/e2e/tests/admin-shell.spec.ts` expects the old Finance/Holded sidebar items and no Pleo group (test code,
  outside the sweep).
- `notification-board.md` still names Feedback as an emitting section; kept as an approved-design snapshot.
- Public `/api/stats/files` (#2292, `FileStatsApiController` in the Shell) is documented in no section doc.

## Proposed for review

None — all candidates resolved this sweep.

## Questions

- Roster for Board: fix the code (read view) or narrow the docs to Admin/TeamsAdmin/coordinators? Tracked in peterdrier/Humans#2306.
- `volunteer-status.md` "Becoming Inactive" still lists "removed from Volunteers team by system sync" beside the
  suspension flow; left as is (unsure whether Inactive is still reachable that way).
- `profile-search-detail.md` says per-result contact-field reads are not cached; may now be cached `UserInfo`.

## Skipped (errors)

- `dev-stats`: `cloc` is not installed in this environment and installing it was refused. Re-run locally.
