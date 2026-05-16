# Freshness sweep — 2026-05-15

**Mode:** diff
**Previous anchor:** `upstream@8f6a2556`
**New anchor:** `upstream@92b7a40a`
**Files changed in upstream window:** 1792
**Mechanical entries dirty:** 11 / 11
**Editorial docs flagged for review:** 90 (triggered) + 10 (unmarked)

## Updated automatically

- `docs-readme-index` — Regenerated docs/README.md Feature Specifications, Section Invariants, and User Guide tables; added 13 features, 10 sections; refreshed descriptions; preserved hand-written prose and other tables.
- `authorization-inventory` — Regenerated full controller/view/handler authorization inventory (637 → 722 lines): added Events Guide, Store, Expenses, Issues, Agent, Container sections; new resource handlers (ContainerAuthorizationHandler, ExpenseReportAuthorizationHandler, IbanAccessHandler, StoreOrderAuthorizationHandler, UserEmailAuthorizationHandler, IssuesAuthorizationHandler, AgentRateLimitHandler, IsAnyTeamManagerOrCoordinatorHandler); new policies (AnyAdminRole, StoreCatalogAdmin, EventsAdminOrAdmin, VolunteerTrackingWrite, AgentRateLimit); ~17 new controllers.
- `controller-architecture-audit` — Regenerated audit from current code: 76 controllers (was 46), 603 actions; preserved curated Purpose/Suggestion entries for 44 controllers still present; added sections for 32 new controllers; removed 2 stale (BoardController, NotificationController); updated header date.
- `dependency-graph` — Regenerated Mermaid diagram: added GoogleGroupSyncService, GoogleRemovalNotificationService, CampRoleService, GovernanceIndexService, AdminDashboardService, TicketTransferService, AttendeeContactImportService, EmailProblemsService, and utility-section nodes (Search/Issues/Store/Expenses/Containers/Mailer); updated edges to reflect Profile/Onboard narrow-interface, TeamResource→Role lazy edge, AuditLog→User, ShiftSign→MembershipCalc, etc.
- `service-data-access-map` — Regenerated map: added 8 new sections (Containers, Mailer, Issues, Events, Expenses, Store, Agent, Search, HumanLifecycle); renamed Profile→Profiles; documented Singleton TrackedCache decorators (Caching{Profile,User,Team,ShiftView}Service) replacing legacy IMemoryCache entries; captured IUserMerge fan-out replacing AccountMergeService direct repo writes; refreshed cross-section violations (BudgetRepository→Teams, CalendarRepository→Teams, EventRepository→EventSettings, CampRepository→Users).
- `data-model-index` — Regenerated entity-index table (in `freshness:auto` block): added Events, Expenses, Store, Issues, Agent rows (28 new entities); removed dead HoldedTransaction/HoldedSyncState row; added ContainerPlacement, TicketTransferRequest, VolunteerBuildStatus to existing grouped rows. All 92 configured entities now indexed.
- `guid-reservations` — Added 0026 block (Event category seeds) discovered in `EventCategoryConfiguration.cs`; existing 0000–0010 entries unchanged.
- `code-analysis-suppressions` — Added `HUM_PROFILE_ISSUSPENDED` and `HUM_USER_NORMALIZEDEMAIL` suppressions; refined MA0016/MA0026/MA0051 descriptions to match analyzer titles; clarified xUnit1051 path.
- `about-page-packages` — no changes needed (already reflects current Directory.Packages.props).
- `reforge-history` — no new days to snapshot (CSV already current).

## Skipped (errors)

- `dev-stats` — `cloc` is not installed on this machine (NUC). `docs/scripts/generate-stats.sh` requires `cloc` for the C# code/comment split and exits with an error if missing. Last sweep also skipped this entry. Action: install `cloc` (`apt install cloc`) on the NUC, or run dev-stats from a machine with `cloc` available.

## Flagged for human review

### From subagent return values

#### `docs/authorization-inventory.md`
- **EventsAdmin* controllers still use role-list `[Authorize]` instead of `PolicyNames.EventsAdminOrAdmin`** — `EventsAdminController`, `EventsDashboardController`, `EventsExportController`, `EventsModerationController` use `[Authorize(Roles = RoleGroups.EventsAdminOrAdmin)]` even though `PolicyNames.EventsAdminOrAdmin` is registered. Two `_Layout.cshtml` blocks (lines 114–115, 122–123) likewise check `User.IsInRole(EventsAdmin) || User.IsInRole(Admin)` instead of `authorize-policy="EventsAdminOrAdmin"`.
  Follow-up: migrate these four controllers and the two layout blocks to policy form during the next Phase 1 pass.
- **`DevSeedController.ResetDashboard` uses `[Authorize(Roles = RoleNames.Admin)]`** instead of `[Authorize(Policy = PolicyNames.AdminOnly)]` — outlier in an otherwise policy-based codebase.
  Follow-up: trivial swap to `PolicyNames.AdminOnly`.
- **`ProfileController` has ~17 repeated `AuthorizeAsync(User, userId, UserEmailOperations.Edit)` call sites** across email-edit endpoints.
  Follow-up: consider extracting an `[AuthorizeUserEmail]` action filter or a helper on `HumansControllerBase`.

#### `docs/controller-architecture-audit.md`
- **32 newly added controllers got name-derived default Purpose lines** (e.g., `Iban` → "Iban", `SetCampSeasonEeSlotCount` → "Set camp season ee slot count"). These need a human pass to write meaningful one-liners.
  Follow-up: manually edit Purpose for `ExpensesController`, `IssuesController`, `StoreController`, `EventsController`, `EventsAdminController`, `NotificationsController`, `ProfileAdminController`, and the various TicketTransfer/Onboarding/Agent controllers.
- **Part 2 "Status of the original (#261) splits" narrative may be stale** — still says "NotificationController is its own section" but the current code has it as `NotificationsController` (plural); ProfileController split commentary may already be partly satisfied by the new `ProfileAdminController`; TeamController Birthdays/Map/Roster references should be checked.
- **EmailController rename summary may be stale** — entries like `EmailOutbox` → `Outbox` still listed as outstanding; verify against any work that has shipped.

#### `docs/architecture/dependency-graph.md`
- **Cycle #5 "GoogleWorkspaceSync ↔ TeamResource" is no longer mutual** — `TeamResourceService` no longer eagerly injects `IGoogleWorkspaceSyncService`. `GSyncSvc` still lazy-resolves TRes, but the reverse edge is gone, so it's a one-way lazy edge now.
  Follow-up: update Cycles section item #5 to describe a one-way lazy edge (or remove it).
- **`ProfileService` fan-in row is stale** — claims outbound edges "User, MembershipCalc, Consent, Role, Audit"; actual outbound is now User, MembershipCalc, Onboard (via narrow `IOnboardingEligibilityQuery`), Audit. Consent and Role were removed; a new edge to OnboardingService via the narrow eligibility-query interface was added.
  Follow-up: update Fan-in hotspots row, explain the new narrow-interface pattern that breaks the Profile↔Onboard ctor cycle.
- **Fan-in counts in the Fan-in hotspots table are stale** — TeamService, UserService, AuditLogService, UserEmailService, and others have additional dependents now visible in the diagram (Search, Issues, Store, ExpenseReport, GovIndex, AdminDash, TicketTransfer, AttendeeImport, MailerImport, EmailProb, CampRole, GoogleGroupSync, GoogleRemoval).
  Follow-up: recount fan-in (eager + lazy) for each row.
- **New cycle path through `GoogleGroupSyncService` not documented** — TRes → Role → SystemTeamSync → GoogleGroupSync → TRes is the motivation for `TeamResourceService`'s lazy-Role pattern.
  Follow-up: add a cycles entry describing this path.
- **`linkStyle` index range was recomputed to 237..252** based on current edge ordering (236 eager + 1 "pending" dashed + 16 lazy). Recompute again if anyone re-orders or adds edges.

#### `docs/architecture/data-model.md`
- **Cross-section FK graph still references removed entities `HoldedTransaction` and `HoldedSyncState`** (Finance section). Replaced by `ExpenseReport`/`ExpenseLine`/`ExpenseAttachment`/`HoldedExpenseOutboxEvent` (Expenses section). These references are outside the `freshness:auto` block and need a manual prose edit.
  Follow-up: update the Cross-section FK graph block; verify whether Finance section still owns any entities.
- **Events section uses different display names than the C# class names** — section doc uses `GuideEvent`, `ModerationAction`, `GuideSettings`, `GuideSharedVenue`, `UserEventFavourite`, `UserGuidePreference`; C# class names are `Event`, `EventModerationAction`, `EventGuideSettings`, `EventVenue`, `EventFavourite`, `EventPreference`. The index now uses C# names — readers following links may be momentarily confused.
  Follow-up: align either section-doc subsection headings or the entity class names.

#### `docs/architecture/service-data-access-map.md` and `docs/architecture/design-rules.md`
- **`design-rules.md` §8 lists Finance section with `HoldedSyncService`/`HoldedTransactionService` owning `holded_transactions`/`holded_sync_states`** — neither these services nor these DbSet declarations exist in code. Only `HoldedExpenseOutboxEvents` (Expenses-owned) and the Holded client (Infrastructure) are present.
  Follow-up: verify whether the Finance/Holded section is planned-but-unbuilt (mark as planned) or was retired (remove the row).
- **`design-rules.md` §8 has the Agent section listed twice** (lines 267 and 269) with identical service lists — duplicate row.
  Follow-up: drop the duplicate Agent row at design-rules.md:269.
- **`DriveActivityMonitorRepository` reads Users, AuditLogEntries, and SystemSettings directly** — largest remaining §15 violation surface.
  Follow-up: inject `IUserService` + `IAuditLogService`; shrink the repo to GoogleResources + SystemSettings(own-key)-only.
- **`TicketRepository.GetAllUserEmailLookupEntriesAsync` reaches into `Set<UserEmail>`** (Profiles section) for attendee matching — the cleanest remaining single-fix cross-section violation.
  Follow-up: add a bulk lookup method to `IUserEmailService` and remove the cross-section read.
- **`DuplicateAccountService` still injects `IUserRepository`/`IProfileRepository` directly** — `AccountMergeService` has already converged on the `IUserMerge` aggregator.
  Follow-up: migrate `DuplicateAccountService` to the same `IEnumerable<IUserMerge>` fan-out.
- **Eleven direct `_cache.InvalidateNobodiesTeamEmails()` call sites in `ProfileController`**, plus duplicates in `TeamAdminController` and `GoogleController` — last meaningful out-of-service cache write surface.
  Follow-up: hide `NobodiesTeamEmails_All` behind a service interface (Google or Email section) so controllers stop touching `IMemoryCache`.

### Editorial docs triggered (`freshness:flag-on-change`) — 90 docs

Listed by path (each was flagged because at least one of its declared trigger globs matched the upstream diff). Each doc carries its own `flag-on-change` reason inline.

<details>
<summary>Expand for full list</summary>

```
docs/architecture/code-review-rules.md
docs/architecture/coding-rules.md
docs/architecture/conventions.md
docs/architecture/design-rules.md
docs/features/47-volunteer-tracking.md
docs/features/audit-log/audit-log.md
docs/features/auth/authentication.md
docs/features/auth/magic-link-auth.md
docs/features/budget/budget.md
docs/features/calendar/community-calendar.md
docs/features/campaigns/campaigns.md
docs/features/camps/camps.md
docs/features/city-planning/city-planning.md
docs/features/email/email-flag-violations-remediation.md
docs/features/email/email-outbox.md
docs/features/expires-on-deadline.md
docs/features/feedback/feedback-system.md
docs/features/global/administration.md
docs/features/global/background-jobs.md
docs/features/global/gdpr-export.md
docs/features/global/global-search.md
docs/features/google-integration/drive-activity-monitoring.md
docs/features/google-integration/google-integration.md
docs/features/google-integration/google-removal-notifications.md
docs/features/google-integration/workspace-account-provisioning.md
docs/features/governance/asociado-applications.md
docs/features/governance/board-voting.md
docs/features/governance/membership-status.md
docs/features/governance/membership-tiers.md
docs/features/guide/in-app-guide.md
docs/features/issues/issues-system.md
docs/features/legal-and-consent/legal-documents-consent.md
docs/features/notifications/notification-inbox.md
docs/features/onboarding/onboarding-pipeline.md
docs/features/onboarding/volunteer-status.md
docs/features/profiles/communication-preferences.md
docs/features/profiles/contact-accounts.md
docs/features/profiles/contact-fields.md
docs/features/profiles/dietary-medical-nudge.md
docs/features/profiles/preferred-email.md
docs/features/profiles/profile-pictures-birthdays.md
docs/features/profiles/profile-search-detail.md
docs/features/profiles/profiles.md
docs/features/shifts/coordinator-roles.md
docs/features/shifts/shift-management.md
docs/features/shifts/shift-preference-wizard.md
docs/features/shifts/shift-signup-visibility.md
docs/features/store/store.md
docs/features/teams/hidden-teams.md
docs/features/teams/teams.md
docs/features/tickets/event-participation.md
docs/features/tickets/ticket-transfer.md
docs/features/tickets/ticket-vendor-integration.md
docs/guide/Admin.md
docs/guide/Budget.md
docs/guide/Campaigns.md
docs/guide/Camps.md
docs/guide/CityPlanning.md
docs/guide/Email.md
docs/guide/Feedback.md
docs/guide/GoogleIntegration.md
docs/guide/Governance.md
docs/guide/LegalAndConsent.md
docs/guide/Onboarding.md
docs/guide/Profiles.md
docs/guide/Shifts.md
docs/guide/Teams.md
docs/guide/Tickets.md
docs/sections/AuditLog.md
docs/sections/Auth.md
docs/sections/Budget.md
docs/sections/Calendar.md
docs/sections/Campaigns.md
docs/sections/Camps.md
docs/sections/CityPlanning.md
docs/sections/Containers.md
docs/sections/Email.md
docs/sections/Expenses.md
docs/sections/Feedback.md
docs/sections/GoogleIntegration.md
docs/sections/Governance.md
docs/sections/Guide.md
docs/sections/Issues.md
docs/sections/LegalAndConsent.md
docs/sections/Notifications.md
docs/sections/Onboarding.md
docs/sections/Profiles.md
docs/sections/Shifts.md
docs/sections/Store.md
docs/sections/Teams.md
docs/sections/Tickets.md
docs/sections/Users.md
docs/seed-data.md
```

</details>

### Unmarked editorial — review for drift

These editorial docs have no `freshness:triggers` / `freshness:flag-on-change` markers, so the sweep can't tell whether they need review. Spot-check whether the section/feature still matches the code and either add markers or update content:

- `docs/features/26-events.md`
- `docs/features/27-guide-browser.md`
- `docs/features/43-google-group-membership-sync.md`
- `docs/features/agent/agent-section.md`
- `docs/features/scanner/scanner-barcode.md`
- `docs/sections/Agent.md`
- `docs/sections/Events.md`
- `docs/sections/Mailer.md`
- `docs/sections/Scanner.md`
- `docs/sections/admin-shell.md`

## Questions

None.
