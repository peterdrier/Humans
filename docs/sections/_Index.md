<!-- freshness:triggers
  docs/sections/*.md
  src/Sections/**
  src/Humans.Web/Controllers/**
  src/Humans.Application/Services/**
  src/Humans.Infrastructure/Repositories/**
  src/Humans.Infrastructure/Data/Configurations/**
-->
<!-- freshness:flag-on-change
  Code-derived section map — the controllers/orchestrators/services/repositories/tables rows must match code (code is authoritative), and the section list must match docs/sections/ directory contents. Regenerate when sections move or new controllers/services/repos/tables land.
-->

# Sections Index — Controllers / Orchestrators / Services / Repositories / Tables

A code-derived map of every section to the concrete classes that implement it. Use it to answer "which controller/service/repository/table belongs to section X" at a glance, and to spot drift (a controller with no owning section, a service with no repository, a table owned by two repos).

A section that has moved into its own project (nobodies-collective/Humans#866, G5) carries its
invariants doc inside that project rather than in this folder; this index is the map to it. The
move recipe is [`G5-SECTION-TEMPLATE.md`](G5-SECTION-TEMPLATE.md).

| Section | Project | Invariants doc |
|---|---|---|
| Agent | `src/Sections/Humans.Agent` | [Agent.md](../../src/Sections/Humans.Agent/Docs/Agent.md) |
| Calendar | `src/Sections/Humans.Calendar` | [Calendar.md](../../src/Sections/Humans.Calendar/Docs/Calendar.md) |
| Campaigns | `src/Sections/Humans.Campaigns` | [Campaigns.md](../../src/Sections/Humans.Campaigns/Docs/Campaigns.md) |
| Cantina | `src/Sections/Humans.Cantina` | [Cantina.md](../../src/Sections/Humans.Cantina/Docs/Cantina.md) |
| Budget | `src/Sections/Humans.Budget` | [Budget.md](../../src/Sections/Humans.Budget/Docs/Budget.md) |
| Consent | `src/Sections/Humans.Consent` | [Consent.md](../../src/Sections/Humans.Consent/Docs/Consent.md) |
| City Planning | `src/Sections/Humans.CityPlanning` | [CityPlanning.md](../../src/Sections/Humans.CityPlanning/Docs/CityPlanning.md) |
| Containers | `src/Sections/Humans.Containers` | [Containers.md](../../src/Sections/Humans.Containers/Docs/Containers.md) |
| Debug | `src/Sections/Humans.Debug` | [Debug.md](../../src/Sections/Humans.Debug/Docs/Debug.md) |
| Development | `src/Sections/Humans.Development` | [Development.md](../../src/Sections/Humans.Development/Docs/Development.md) |
| Email | `src/Sections/Humans.Email` | [Email.md](../../src/Sections/Humans.Email/Docs/Email.md) |
| Feedback | `src/Sections/Humans.Feedback` | [Feedback.md](../../src/Sections/Humans.Feedback/Docs/Feedback.md) |
| Finance | `src/Sections/Humans.Finance` | [Finance.md](../../src/Sections/Humans.Finance/Docs/Finance.md) |
| Event Guide | `src/Sections/Humans.Events` | [Events.md](../../src/Sections/Humans.Events/Docs/Events.md) |
| Expenses | `src/Sections/Humans.Expenses` | [Expenses.md](../../src/Sections/Humans.Expenses/Docs/Expenses.md) |
| Gate | `src/Sections/Humans.Gate` | [Gate.md](../../src/Sections/Humans.Gate/Docs/Gate.md) |
| Gdpr | `src/Sections/Humans.Gdpr` | [Gdpr.md](../../src/Sections/Humans.Gdpr/Docs/Gdpr.md) |
| Governance | `src/Sections/Humans.Governance` | [Governance.md](../../src/Sections/Humans.Governance/Docs/Governance.md) |
| Holded | `src/Sections/Humans.Holded` | [Holded.md](../../src/Sections/Humans.Holded/Docs/Holded.md) — the ledger mirror; the HTTP connector stays in Base and keeps [its own doc here](Holded.md) |
| Guide | `src/Sections/Humans.Guide` | [Guide.md](../../src/Sections/Humans.Guide/Docs/Guide.md) |
| Issues | `src/Sections/Humans.Issues` | [Issues.md](../../src/Sections/Humans.Issues/Docs/Issues.md) |
| Notifications | `src/Sections/Humans.Notifications` | [Notifications.md](../../src/Sections/Humans.Notifications/Docs/Notifications.md) |
| Onboarding | `src/Sections/Humans.Onboarding` | [Onboarding.md](../../src/Sections/Humans.Onboarding/Docs/Onboarding.md) |
| Scanner | `src/Sections/Humans.Scanner` | [Scanner.md](../../src/Sections/Humans.Scanner/Docs/Scanner.md) |
| Search | `src/Sections/Humans.Search` | [Search.md](../../src/Sections/Humans.Search/Docs/Search.md) |
| Store | `src/Sections/Humans.Store` | [Store.md](../../src/Sections/Humans.Store/Docs/Store.md) |
| Surveys | `src/Sections/Humans.Surveys` | [Surveys.md](../../src/Sections/Humans.Surveys/Docs/Surveys.md) |
| System Settings | `src/Sections/Humans.SystemSettings` | — (no invariants doc; one key/value table) |

**This table is derived from code, not from the section docs — code is authoritative.** Regenerate it when sections move:

- **Controllers** — `src/Humans.Web/Controllers/*.cs`, assigned by `[Route(...)]` prefix (and constructor dependencies where there is no route attribute). Infrastructure/base controllers (`HumansControllerBase`, `HumansTeamControllerBase`, `HumansCampControllerBase`, `ApiControllerBase`, `HomeController`, `AboutController`) are excluded.
- **Orchestrators** — service classes that inject **no `I*Repository`** and coordinate one or more other services. Per [`peters-hard-rules.md`](../architecture/peters-hard-rules.md): "Some services are orchestrators, organizing calls to multiple services. These should not call repositories."
- **Services** — service classes that own/inject a repository. Caching decorators (Infrastructure) are listed in italics.
- **Repositories** — `*Repository.cs` under `src/Humans.Infrastructure/Repositories/` or a section project's `Data/`. Per the hard rules, only the repository may touch its section's tables.
- **Tables** — EF `ToTable(...)` under `src/Humans.Infrastructure/Data/Configurations/` or a section project's `Data/Configurations/`.

Cross-check against [`design-rules.md` §8 (Table Ownership Map)](../architecture/design-rules.md#8-table-ownership-map). Where this table and §8 disagree, the divergence is a drift bug in one of them — fix it.

## Vertical vs cross-cutting

- **Vertical sections** are the business domains — basically everything.
- **Cross-cutting concerns** are the technical services the business verticals use: Auth, Audit, Notifications, GDPR. Per [`peters-hard-rules.md`](../architecture/peters-hard-rules.md), these are *horizontal* sections and must **not** reference vertical sections — that would create cycles in the call graph.

## Vertical sections

| Section | Controllers | Orchestrators | Services | Repositories | Tables |
|---------|-------------|---------------|----------|--------------|--------|
| **Agent** | `AgentController`, `AgentApiController`, `AdminAgentController` (`Humans.Agent.Controllers`) | — | `AgentService`, `AgentAdminStatusService`, `AgentSettingsService`, `AgentPromptAssembler`, `AgentToolDispatcher`, `AgentUserSnapshotProvider`, `AgentAbuseDetector`, `AnthropicClient` (`Humans.Agent.Services`) | `AgentRepository` / `IAgentRepository` (`Humans.Agent.Data`) | `agent_conversations`, `agent_messages`, `agent_settings` |
| **Budget** | `BudgetController`, `BudgetAdminController` (the latter routed under `/Finance`) (`Humans.Budget.Controllers`) | `TicketingBudgetService` (`Humans.Budget.Services`) | `BudgetService` (`Humans.Budget.Services`) | `BudgetRepository` / `IBudgetRepository` (`Humans.Budget.Data`) | `budget_years`, `budget_groups`, `budget_categories`, `budget_line_items`, `budget_audit_logs`, `ticketing_projections` |
| **Calendar** | `CalendarController` (`src/Sections/Humans.Calendar`) | — | `CalendarService`, *`CachingCalendarService`* | `CalendarRepository` | `calendar_events`, `calendar_event_exceptions` |
| **Campaigns** | `CampaignController` (section) | — | `CampaignService` (section) | `CampaignRepository` (section) | `campaigns`, `campaign_codes`, `campaign_grants` |
| **Camps** | `CampController`, `CampAdminController`, `CampApiController`, `CampComplianceController` | `CampContactService` | `CampService`, `CampRoleService`, *`CachingCampService`* | `CampRepository` | `camps`, `camp_seasons`, `camp_members`, `camp_images`, `camp_historical_names`, `camp_settings`, `camp_role_definitions`, `camp_role_assignments` |
| **City Planning** | `CityPlanningController`, `CityPlanningApiController` (`Humans.CityPlanning.Controllers`) | — | `CityPlanningService` (`Humans.CityPlanning.Services`) | `CityPlanningRepository` / `ICityPlanningRepository` (`Humans.CityPlanning.Data`) | `city_planning_settings`, `camp_polygons`, `camp_polygon_histories` |
| **Containers** | `ContainerController` (`Humans.Containers.Controllers`) | — | `Service` (`Humans.Containers.Services`) | `Repository` / `IContainerRepository` (`Humans.Containers.Data`) | `containers`, `container_placements` |
| **Email** | `EmailController` (`Humans.Email.Controllers`) | — | `EmailOutboxService`, `OutboxEmailService`, `EmailOutboxProcessor`, `EmailMessageFactory`, `EmailRenderer` (`Humans.Email.Services`) | `EmailOutboxRepository` / `IEmailOutboxRepository` (`Humans.Email.Data`) | `email_outbox_messages`, `system_settings` (key `IsEmailSendingPaused`) |
| **Event Guide** | `EventsController`, `EventsAdminController`, `EventsDashboardController`, `EventsExportController`, `EventsModerationController`, `EventsApiController` (`Humans.Events.Controllers`) | — | `EventService`, *`CachingEventService`* (`Humans.Events.Services`) | `EventRepository` / `IEventRepository` (`Humans.Events.Data`) | `events`, `event_categories`, `event_venues`, `event_guide_settings`, `event_moderation_actions`, `event_favourites`, `event_preferences` |
| **Expenses** | `ExpensesController` (`Humans.Expenses.Controllers`) | — | `ExpenseReportService` (`Humans.Expenses.Services`) | `ExpenseRepository` / `IExpenseRepository` (`Humans.Expenses.Data`) | `expense_reports`, `expense_lines`, `expense_attachments`, `holded_expense_outbox_events` |
| **Feedback** | `FeedbackController`, `FeedbackApiController` (section) | — | `FeedbackService` (section) | `FeedbackRepository` (section) | `feedback_reports`, `feedback_messages` |
| **Finance** | `FinanceController` (`Humans.Finance.Controllers`) — the Holded/creditor half of the old `/Finance` controller; the Budget-CRUD half stayed in Shell as `BudgetAdminController` under the same route prefix | — | `Service` (`Humans.Finance.Services`) | `Repository` / `IHoldedRepository` (`Humans.Finance.Data`) | `holded_doc_sync_state`, `holded_category_map`, `holded_expense_docs`, `holded_creditor_contacts` |
| **Holded** | `HoldedController` (`Humans.Holded.Controllers`) — `/Holded` admin screen + `/Holded/Accounts/{number}` GL page | — | `Service` (`Humans.Holded.Services`) | `Repository` / `IHoldedMirrorRepository` (`Humans.Holded.Data`) | `holded_ledger_lines`, `holded_accounts`, `holded_api_calls`, `holded_sync_states` |
| **Gate** | `GateController`, `GateVendorBackfillAdminController` (`Humans.Gate.Controllers`) | — | `GateService` (`Humans.Gate.Services`) | `GateRepository` / `IGateRepository` (`Humans.Gate.Data`) | `gate_scan_events`, `gate_settings`, `gate_staff_pins` |
| **Governance** | `GovernanceController`, `GovernanceApplicationsController`, `GovernanceBoardVotingController` (`Humans.Governance.Controllers`) | `GovernanceIndexService`, `MembershipCalculator` (`Humans.Governance.Services`) | `ApplicationDecisionService` (`Humans.Governance.Services`) | `ApplicationRepository` / `IApplicationRepository` (`Humans.Governance.Data`) | `applications`, `application_state_history`, `board_votes` |
| **Google Integration** | `GoogleController` | `GoogleGroupSyncService`, `GoogleAdminService`, `EmailProvisioningService`, `GoogleRemovalNotificationService` | `GoogleWorkspaceSyncService`, `GoogleWorkspaceUserService`, `DriveActivityMonitorService`, `SyncSettingsService`, `TeamResourceService`, Google clients (`GoogleDirectoryClient`, `GoogleGroupMembershipClient`, `GoogleGroupProvisioningClient`, `GoogleDriveActivityClient`, `GoogleDrivePermissionsClient`, `WorkspaceUserDirectoryClient`) | `GoogleResourceRepository`, `GoogleSyncOutboxRepository`, `DriveActivityMonitorRepository`, `SyncSettingsRepository` | `google_resources`, `google_sync_outbox`, `sync_service_settings`, `system_settings` (key `DriveActivityMonitor:LastRunAt`) |
| **Guide** | `GuideController` (`Humans.Guide.Controllers`) | — | `GuideContentService`, `GuideRoleResolver`, `GuideRenderer` (`Humans.Guide.Services`); `GitHubGuideContentSource` stays in `Humans.Infrastructure` — a shared GitHub-markdown fetcher, three of whose four consumers are not Guide's | — | — (content served from GitHub `docs/guide/`, cached via `IMemoryCache`) |
| **Holded Connector** | — (no UI) | — | `HoldedClient`, `HoldedCallLog` (`Humans.Infrastructure.Services.Holded`) | — | — (thin API client in Base; owns no tables — see [`Holded.md`](Holded.md)) |
| **Issues** | `IssuesController`, `IssuesApiController` (`Humans.Issues.Controllers`) | — | `IssuesService` (`Humans.Issues.Services`) | `IssuesRepository` / `IIssuesRepository` (`Humans.Issues.Data`) | `issues`, `issue_comments` |
| **Consent** | `LegalController`, `AdminLegalDocumentsController`, `ConsentController` (`Humans.Consent.Controllers`) | — | `LegalDocumentService`, `LegalDocumentSyncService`, `ConsentService`, `LegalDocumentSyncRunner`, *`CachingLegalDocumentSyncService`*, *`CachingConsentService`* (`Humans.Consent.Services`) | `LegalDocumentRepository`, `ConsentRepository` (`Humans.Consent.Data`) | `legal_documents`, `document_versions`, `consent_records` |
| **Profiles** | `ProfileController`, `ProfileApiController`, `ProfileAdminController`, `ProfileBackfillAdminController`, `ProfilePictureMigrationAdminController`, `AdminDuplicateAccountsController`, `AdminMergeController` | `ProfileEditorService`, `EmailProblemsService`, `AdminHumanListAssembler` | `ProfileService`, `ContactFieldService`, `CommunicationPreferenceService`, `UserEmailService`, `AccountMergeService`, `DuplicateAccountService` | `AccountMergeRepository`, `CommunicationPreferenceRepository` (+ `ProfileService` via `UserRepository`) | `profiles`, `profile_languages`, `contact_fields`, `user_emails`, `communication_preferences`, `volunteer_history_entries`, `account_merge_requests` |
| **Shifts** | `ShiftsController`, `ShiftAdminController`, `ShiftDashboardController`, `ShiftWorkloadAdminController`, `VolunteerTrackingController` | — | `ShiftManagementService`, `ShiftSignupService`, `GeneralAvailabilityService`, `VolunteerTrackingService`, `VolunteerTrackingExportService`, `ShiftViewService`, `RotaCoordinatorMessageService`, `BurnSettingsService`, `WorkloadService`, *`CachingShiftViewService`* | `VolunteerTrackingRepository` (+ shift/rota repos) | `rotas`, `shifts`, `shift_signups`, `shift_tags`, `rota_shift_tags`, `event_settings`, `general_availability`, `volunteer_event_profiles`, `volunteer_build_statuses`, `volunteer_tag_preferences`, `event_participations` |
| **Store** | `StoreController`, `StoreAdminController`, `StoreStripeWebhookController` | — | `Service` (`Humans.Store.Services`) | `Repository` / `IStoreRepository` (`Humans.Store.Data`) | `store_products`, `store_orders`, `store_order_lines`, `store_payments`, `store_invoices`, `store_treasury_sync_state` |
| **Surveys** | `SurveyController`, `SurveyAdminController`, `SurveysApiController` (`Humans.Surveys.Controllers`) | — | `SurveyService` (`Humans.Surveys.Services`) | `SurveyRepository` / `ISurveyRepository` (`Humans.Surveys.Data`) | `surveys`, `survey_questions`, `survey_question_options`, `survey_invitations`, `survey_responses`, `survey_answers` |
| **Teams** | `TeamController`, `TeamAdminController` | — | `TeamService`, `TeamPageService`, *`CachingTeamService`* | `TeamRepository` | `teams`, `team_members`, `team_join_requests`, `team_join_request_state_history`, `team_role_definitions`, `team_role_assignments`, `team_early_entry_grants` |
| **Tickets** | `TicketController`, `TicketTransferController`, `TicketTransferAdminController`, `TicketsContactsAdminController`, `TicketsOnsiteAdminController` | `OnsiteRosterService` | `TicketQueryService`, `TicketSyncService`, `TicketTransferService`, `AttendeeContactImportService`, *`CachingTicketQueryService`* | `TicketRepository`, `TicketTransferRepository` | `ticket_orders`, `ticket_attendees`, `ticket_sync_state`, `ticket_transfer_requests` |
| **Users / Identity** | `UsersAdminDebugController`, `UnsubscribeController`, `LanguageController` | `AccountDeletionService`, `UserParticipationBackfillService`, `ExternalLoginService` | `UserService`, `AccountProvisioningService`, `UnsubscribeService`, `UserEmailProviderBackfillService`, *`CachingUserService`* | `UserRepository` | `AspNetUsers`, `AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserTokens`, `AspNetRoles` (legacy), `AspNetUserRoles` (legacy) |
| **Onboarding** | `OnboardingReviewController`, `OnboardingWidgetController` (`Humans.Onboarding.Controllers`), `WelcomeController` (Shell) | `OnboardingService` (`Humans.Onboarding.Services`, internal) | `OnboardingWidgetState` (`Humans.Onboarding.Services`, internal) | — | — (owns no tables; orchestrates Profiles, Consent, Teams and Governance through their service interfaces) |
| **Human Lifecycle** | — (admin actions via `AdminController`) | `HumanLifecycleService` | — | — | — |
| **Early Entry** | `EarlyEntryRosterController` | `EarlyEntryService` | *`CachingEarlyEntryService`* | — | — |
| **Cantina** | `CantinaController` (`Humans.Cantina.Controllers`) | — | `CantinaRosterService` (`Humans.Cantina.Services`) | — | — (reads Shifts via `IShiftManagementService`; owns no tables) |
| **Dashboard** | — (rendered on Home) | `DashboardService`, `AdminDashboardService` | — | — | — |
| **Search** | `SearchController` (`Humans.Search.Controllers`, internal) | `SearchService` (`Humans.Search.Services`, internal) | — | — | — (owns no tables; fans out to Users, Teams, Camps, Shifts and Events through their service interfaces) |
| **Mailer** | `MailerAdminController` (`Humans.Mailer.Controllers`) | `MailerImportService`, `MailerAudienceSyncService` | `MailerLiteClient` (`Humans.Mailer.Services.MailerLite`) | — | — (MailerLite is the system of record; in-Humans writes route through other sections' services) |
| **Scanner** | `ScannerController` (`src/Sections/Humans.Scanner`) | — | — | — | — (presentational; owns no tables) |
| **Debug** | `DebugController` (`Humans.Debug.Controllers`), `LogApiController`, `ColorPaletteController`, `WidgetGalleryController`, `TimezoneApiController` | — | — | `AdminDatabaseDiagnosticsRepository` | — (Debug owns no tables; it reads in-memory trackers and `IAdminDatabaseDiagnosticsService`) |
| **Development** | `DevLoginController`, `DevSeedController` (`Humans.Development.Controllers`) | — | `DevPersonaSeeder`, `DevelopmentCampRoleSeeder`, `DevelopmentDashboardSeeder` (`Humans.Development.Services`) — dev fixture seeders, not application services; registered outside Production only | — | — (dev-only tooling; owns no tables and writes only through other sections' services) |

## Cross-cutting concerns

The technical services the business verticals use. Per the hard rules these are horizontal sections and must **not** reference vertical sections.

| Section | Controllers | Orchestrators | Services | Repositories | Tables |
|---------|-------------|---------------|----------|--------------|--------|
| **Audit Log** | `AuditLogController` | `AuditViewerService` | `AuditLogService` | `AuditLogRepository` | `audit_log` |
| **Auth** | `AccountController` | `MagicLinkService` | `RoleAssignmentService`, `AdminAuthorizationService`, *`CachingRoleAssignmentService`* | `RoleAssignmentRepository` | `role_assignments` |
| **Notifications** | `NotificationsController` (`Humans.Notifications.Controllers`) | — | `NotificationService`, `NotificationEmitter`, `NotificationInboxService`, `NotificationMeterProvider` (`Humans.Notifications.Services`) | `NotificationRepository` / `INotificationRepository` (`Humans.Notifications.Data`) | `notifications`, `notification_recipients` |
| **GDPR** | — (export download via Shell's `ProfileController` / `GuestController`) | `GdprExportService` (`Humans.Gdpr.Services`, internal) | — | — | — (owns no tables; fans out to every `IUserDataContributor` on `Humans.Gdpr.Contracts`) |
| **Admin Shell** | `AdminController` (`/Admin` dashboard tile only) | — | `AdminNavTree`, `AdminSidebarViewComponent`, `AdminBreadcrumbViewComponent` (Web layer) | — | — (frame only; owns no tables) |

## Notes & known drift

- **`/Admin/*` is not a vertical section.** `AdminController` is a nav holder; the actions it exposes belong to their owning sections (outbox pause → Email, suspend/merge/purge → Profiles/Users, sync settings → Google Integration, role assignments → Auth, legal-doc management → Consent). It *is* listed above as the cross-cutting **Admin Shell** — the logical holder for the admin-type bits each section contributes ([`admin-shell.md`](admin-shell.md) documents the shared frame: sidebar, breadcrumb, dashboard skeleton). Long-term direction is for the shell to become framework plumbing with a dynamically built nav, so keep the row: it is the seam that work lands on, not a vertical section sneaking in.
- **`design-rules.md` §8 divergence (code wins):** §8 still lists `google_resources` and `TeamResourceService` under Teams, but `TeamResourceService` (`Humans.Application.Services.GoogleIntegration`) and `GoogleResourceRepository` (`Humans.Infrastructure.Repositories.GoogleIntegration`) live in Google Integration, matching this table's row above. This is drift to reconcile in §8. (§8's other previously-noted divergences — Event Guide's service name, Camps' `CampRoleService`/`camp_role_*` rows, and the Finance tables list — are resolved as of this sweep.)
- **`event_participations`** is resolved: it is configured under `Data/Configurations/Users/` and owned by `UserRepository`, so Users/Identity is the owner (matching §8). `ShiftsDbContext` has no `EventParticipation` DbSet; Shifts reads participation through the Users service.
- **`SystemDbContext` is not a section.** nobodies-collective/Humans#858 (2026-08-10) added `SystemDbContext` (`src/Humans.Infrastructure/Data/SystemDbContext.cs`) mapping only `DataProtectionKeys` — the platform context for framework-owned tables no section can plausibly own. It has no repository, no service, and no controller; additions are Peter's call by design. Not listed as a table row above because it owns no section-shaped table, but noted here so a reader searching for `DataProtectionKeys` finds it.
