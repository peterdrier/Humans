# Freshness sweep: 2026-10-30

**Mode:** diff
**Previous anchor:** `cb3d8b37`
**New anchor:** `f71de1eb` (upstream/main)
**Worktree base:** `origin/main` @ `f71de1eb` (frozen at start; same commit as upstream/main)
**Diff window:** the "Comments earn their place" commits, resharper cleanups and dependency bumps
touched nearly every file, so docs were matched only against files changed by the other commits.
Every marked editorial doc and every mechanical entry was still dirty.

**Result:** every dirty editorial doc was reviewed and edited where it had drifted. Every verifier in
`run-all.sh` passes except `dev-stats` (environmental, see Skipped); `diff-mode.sh` passes and
`verify-triggers.sh --check` finds no dead triggers.

Most of the drift came from the window's access changes. Board now reads every section's admin pages
read-only (pages moved from `*Policies.Manage` to `*Policies.Read`; writes stay on Manage). Roles are
now section-owned, and several were renamed (Shifts NoInfo, Early Entry Admin). Endpoints are
authenticated by default, with `[AllowBeforeActiveMembership]`. The rest came from moving things into
each section: `Section<Seam>.cs` folded into `Section.cs`, and audit actions and notification sources
now declared per section. Several features were also scoped to an event (barrio seasons, rideshare,
grants).

## Updated automatically

- `dependency-graph`: nodes added (CampImageGallery, CampMembershipNotices, ConsentReminderService,
  CreditorMatchService, FeedbackReplyDelivery, StoreStripePayments), and new edges added
  (CityPlan→Settings, ConsentReview→Audit, ExpenseBookings→Holded, LowIncome→User,
  ShiftMgmt⇢Settings). Stale edges removed: Merge→Team/Notif, DupAcct→Consent, Feedback→Email/NotifEmitter/UEmail,
  Consent→NotifInbox, Team⇢Email, Cal⇢Team. UserNameSyncService and TicketingPreferenceLock removed.
  `linkStyle` re-indexed.
- `service-data-access-map`: Camps (CampEventSettings table; CampImageGallery, CampMembershipNotices),
  Finance (CreditorMatchService), Store (StoreStripePayments), Cantina (IDietaryProfileRepository),
  Rideshare (real table names). Rollup unchanged.
- `authorization-inventory`: the touched section maps and the global file. Agent/Backdoor/Debug are now
  BoardOrAdmin with AdminOnly POSTs. Also covered: the key-authed Backdoor API `[AllowAnonymous]`,
  the `[AllowBeforeActiveMembership]` markers, Shifts RevealMedical, CityPlanning placement schedule,
  Dev login, `AuthorizeAsync` line numbers, and the Team view rows.
- `about-page-packages`: Anthropic 13.3.0, MailKit 4.18.2, Google.Apis.CloudIdentity.v1 1.77.0.4309,
  Markdig 1.5.0, AngleSharp 1.8.6.
- `docs-readme-index`: Grants spec and invariants, LowIncome invariants.
- `guid-reservations`, `code-analysis-suppressions`: checked, no change.
- `reforge-history`: no completed day after the last row (2026-10-29).

## Fixed in place (editorial drift)

- **Architecture:** `design-rules.md` §8b now lists the AuditActionRegistry/IAuditActionContributor and
  NotificationSourceRegistry/INotificationSourceContributor fanouts. In `code-review-rules.md`, under
  authenticated-by-default, a mutating action needs an explicit policy or resource check.
  `roslyn-analysis.md` has the matching note on the POST candidate.
  `AGENTS.md`, `conventions.md`, `coding-rules.md` and `seed-data.md` were verified current.
- **Users/Auth:** guide Profiles (separate dietary/shifts/medical pages; Board read-only); `Auth.md`
  (fallback policy, the Home/Index exception); `Users.md` (folded seams, export failure route);
  `contact-fields.md` (no default visibility, pre-existing); `profile-pictures-birthdays.md`;
  `user-search-overhaul.md` (ExactName, pre-existing).
- **Governance cluster:** guide Governance (Board assigns no roles); `Onboarding.md` (UsersAdmin ≠
  ConsentCoordinator, pre-existing; AllowBeforeActiveMembership; VolunteerApproved retired);
  `onboarding-pipeline.md`; `Consent.md` (current arch-test names).
- **Money:** guides Budget, Store, Expenses (pre-approval managers do the first review);
  `Finance.md`, `Store.md`, `Store-feature.md` (pages on Read, writes on Manage);
  `expense-reimbursement-process.md`.
- **Tickets/Gate:** guide Tickets; `Tickets.md`, `ticket-transfer.md`,
  `ticket-vendor-integration.md` (Board read-only); `Gate.md` and `gate-terminal-login.md` (GateAdmit
  excludes Board); `EarlyEntry.md` (roster on ShiftsPolicies.Read, pre-existing).
- **Events/Camps:** guides Events, Camps (`/Season/{eventSettingsId}`, per-event registration),
  CityPlanning (schedule-driven placement); `Events.md`, `Events-feature.md`, `Calendar.md`,
  `Camps-feature.md`, `CityPlanning.md` (`UpdatePlacementScheduleAsync`), `Cantina.md`,
  `daily-roster.md` and `dietary-nudge.md` (audited medical reveal, no legacy-profile fallback),
  `Rideshare.md`.
- **Shifts/Teams/Google:** guides Shifts, Teams; `Shifts.md`, `shift-management.md`,
  `post-event-stats.md`, `workload-dashboard.md`, `47-volunteer-tracking.md` (Read pages; the day-off
  audit actions were pre-existing drift); `coordinator-roles.md` (section-owned roles);
  `shift-preference-wizard.md`; `Monitor.md`, `drive-activity-monitoring.md`;
  `google-integration.md`; `workspace-account-provisioning.md` (coordinator ProvisionEmail path,
  pre-existing); `Teams-feature.md`.
- **Email/Notifications/Audit:** guide Campaigns, `Campaigns.md`, `Campaigns-feature.md` (Board
  read-only detail); `AuditLog.md` (enum references removed); `MailerLite.md`.
- **Platform:** guides Issues and Admin; `Tour.md`, `Guide.md`, `Search.md` and `feedback-system.md`
  (folded seams); `Development.md`; `Agent-feature.md`; `Settings.md`; `Debug.md`; `client-stats.md`
  (pre-existing).
- **Global:** `admin-shell.md`, `administration.md`, `background-jobs.md` (ExpenseReconJob),
  `gdpr-export.md`, `global-search.md`, `preapprovals.md`.

## Dead trigger globs

### Repaired
- `Shifts.md`, `shift-management.md`, `shift-signup-visibility.md`: `src/Humans.Base/Authorization/ShiftsRoles.cs` →
  `src/Sections/Humans.Shifts.Contracts/ShiftsRoles.cs`
- `Rideshare.md`: `src/Sections/Humans.Notifications.Contracts/RideshareNotificationSources.cs` →
  `src/Sections/Humans.Rideshare/RideshareNotificationSources.cs`

### Unresolved
None.

## Pruned

- `docs/superpowers/plans/2026-09-25-workgroup-budget.md` (under the 5% target). The
  feature has shipped. The plan's global constraints and review-focus rules are already in
  `docs/features/global/workgroup-budget.md` and the Workgroups.md budget invariant
  (`WorkgroupService.SetBudgetAsync`). The rest is task lists and code samples. No inbound refs.

### Not pruned
- Too young: `2026-10-19-users-decomposition-plan.md`. Kept on purpose: the freshness-sweep,
  debt-sweep and section-doctor designs (cited from `.claude/` skills), the Pleo draft, and
  `fk-cut-inventory.md` (cited by code).

## Unmarked editorial (add `freshness:triggers`)

Surveys `ranked-choice-voting.md`, `grid-questions.md`, `survey-preview.md`,
`survey-information-blocks.md`, `survey-intro-markdown.md`, `survey-invitation-email-copy.md`;
Backdoor `finance-read-api.md`; Finance `sepa-payout.md`; Notifications `notification-api.md`.

## Flagged for human review

None. The open judgment calls are listed under Questions.

## Proposed for review

None — all candidates resolved this sweep.

## Questions

1. design-rules §11 / conventions.md: state the authenticated-by-default fallback and `[AllowBeforeActiveMembership]` gate explicitly?
2. design-rules §11 resource-less handler list: add `GrantsPolicyHandler` and `PreapprovalsPolicyHandler`?
3. dependency-graph: give non-service helpers with cross-section edges (TeamMembershipNotices, SystemTeamSyncJob, NonCompliantMemberSuspension, SepaPayoutEmails) nodes?
4. dependency-graph: NotifMeter's `ITicketSync` edge — keep pointing at TicketSync, or switch to TicketQ (the caching decorator)?
5. `SyncLegalDocumentsJob` is public "because Shell names the concrete type", but Consent's own `Section.cs` registers it now. Fix the comment and doc line (and make the job internal)?
6. `notification-board.md` (an approved forward design) says it "replaces the NotificationSource enum", which is already gone. Leave it as is?
7. Update the stale `sidebarMatrix` in `tests/e2e/tests/admin-shell.spec.ts` (code, outside sweep scope)?

## Skipped (errors)

- `dev-stats`: `generate-stats.sh` needs `cloc`, which is not installed in the cloud container and
  could not be installed. reforge-history also has no new completed day, so nothing is lost this run.
  The `dev-stats.sh` verifier also fails here: it reads the local `main` branch, which is stale in a
  fresh cloud clone (2026-10-27), not origin/main.
