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
invariants doc inside that project rather than in this folder; this index is the map to it:

| Section | Project | Invariants doc |
|---|---|---|
| Event Guide | `src/Sections/Humans.Events` | [Events.md](../../src/Sections/Humans.Events/Docs/Events.md) |
| Store | `src/Sections/Humans.Store` | [Store.md](../../src/Sections/Humans.Store/Docs/Store.md) |
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
| **Agent** | `AgentController`, `AgentApiController`, `AdminAgentController` | — | `AgentService`, `AgentAdminStatusService`, `AgentSettingsService`, `AgentPromptAssembler`, `AgentToolDispatcher`, `AgentUserSnapshotProvider`, `AgentAbuseDetector`, `AnthropicClient` | `AgentRepository` | `agent_conversations`, `agent_messages`, `agent_settings` |
| **Budget** | `BudgetController` | — | `BudgetService` | `BudgetRepository` | `budget_years`, `budget_groups`, `budget_categories`, `budget_line_items`, `budget_audit_logs`, `ticketing_projections` |
| **Calendar** | `CalendarController` | — | `CalendarService`, *`CachingCalendarService`* | `CalendarRepository` | `calendar_events`, `calendar_event_exceptions` |
| **Campaigns** | `CampaignController` | — | `CampaignService` | `CampaignRepository` | `campaigns`, `campaign_codes`, `campaign_grants` |
| **Camps** | `CampController`, `CampAdminController`, `CampApiController` | `CampContactService` | `CampService`, `CampRoleService`, *`CachingCampService`* | `CampRepository` | `camps`, `camp_seasons`, `camp_members`, `camp_images`, `camp_historical_names`, `camp_settings`, `camp_role_definitions`, `camp_role_assignments` |
| **City Planning** | `CityPlanningController`, `CityPlanningApiController` | — | `CityPlanningService` | `CityPlanningRepository` | `city_planning_settings`, `camp_polygons`, `camp_polygon_histories` |
| **Containers** | `ContainerController` | — | `ContainerService` | `ContainerRepository` | `containers`, `container_placements` |
| **Email** | `EmailController` | — | `EmailOutboxService`, `OutboxEmailService` | `EmailOutboxRepository` | `email_outbox_messages`, `system_settings` (key `email_outbox_paused`) |
| **Event Guide** | `EventsController`, `EventsAdminController`, `EventsDashboardController`, `EventsExportController`, `EventsModerationController`, `EventsApiController` (`Humans.Events.Controllers`) | — | `EventService`, *`CachingEventService`* (`Humans.Events.Services`) | `EventRepository` / `IEventRepository` (`Humans.Events.Data`) | `events`, `event_categories`, `event_venues`, `event_guide_settings`, `event_moderation_actions`, `event_favourites`, `event_preferences` |
| **Expenses** | `ExpensesController` | — | `ExpenseReportService` | `ExpenseRepository` | `expense_reports`, `expense_lines`, `expense_attachments`, `holded_expense_outbox_events` |
| **Feedback** | `FeedbackController`, `FeedbackApiController` | — | `FeedbackService` | `FeedbackRepository` | `feedback_reports`, `feedback_messages` |
| **Finance** | `FinanceController` | — | `HoldedFinanceService` | `HoldedRepository` | `holded_sync_states`, `holded_category_map`, `holded_expense_docs`, `holded_ledger_lines`, `holded_creditor_contacts` |
| **Gate** | `GateController` | — | `GateService` | `GateRepository` | `gate_scan_events`, `gate_settings` |
| **Governance** | `GovernanceController`, `GovernanceApplicationsController`, `GovernanceBoardVotingController` | `GovernanceIndexService` | `ApplicationDecisionService` | `ApplicationRepository` | `applications`, `application_state_history`, `board_votes` |
| **Google Integration** | `GoogleController` | `GoogleGroupSyncService`, `GoogleAdminService`, `EmailProvisioningService`, `GoogleRemovalNotificationService` | `GoogleWorkspaceSyncService`, `GoogleWorkspaceUserService`, `DriveActivityMonitorService`, `SyncSettingsService`, `TeamResourceService`, Google clients (`GoogleDirectoryClient`, `GoogleGroupMembershipClient`, `GoogleGroupProvisioningClient`, `GoogleDriveActivityClient`, `GoogleDrivePermissionsClient`, `WorkspaceUserDirectoryClient`) | `GoogleResourceRepository`, `GoogleSyncOutboxRepository`, `DriveActivityMonitorRepository`, `SyncSettingsRepository` | `google_resources`, `google_sync_outbox`, `sync_service_settings`, `system_settings` (key `DriveActivityMonitor:LastRunAt`) |
| **Guide** | `GuideController` | — | `GuideContentService`, `GuideRoleResolver`, `GuideRenderer`, `GitHubGuideContentSource` | — | — (content served from GitHub `docs/guide/`, cached via `IMemoryCache`) |
| **Holded** | — (no UI) | — | `HoldedClient` | — | — (thin API client; owns no tables) |
| **Issues** | `IssuesController`, `IssuesApiController` | — | `IssuesService` | `IssuesRepository` | `issues`, `issue_comments` |
| **Consent** | `LegalController`, `AdminLegalDocumentsController`, `ConsentController` | — | `LegalDocumentService`, `LegalDocumentSyncService`, `ConsentService`, *`CachingLegalDocumentSyncService`*, *`CachingConsentService`* | `LegalDocumentRepository`, `ConsentRepository` | `legal_documents`, `document_versions`, `consent_records` |
| **Profiles** | `ProfileController`, `ProfileApiController`, `ProfileAdminController`, `ProfileBackfillAdminController`, `ProfilePictureMigrationAdminController`, `AdminDuplicateAccountsController`, `AdminMergeController` | `ProfileEditorService`, `EmailProblemsService`, `AdminHumanListAssembler` | `ProfileService`, `ContactFieldService`, `CommunicationPreferenceService`, `UserEmailService`, `AccountMergeService`, `DuplicateAccountService` | `AccountMergeRepository`, `CommunicationPreferenceRepository` (+ `ProfileService` via `UserRepository`) | `profiles`, `profile_languages`, `contact_fields`, `user_emails`, `communication_preferences`, `volunteer_history_entries`, `account_merge_requests` |
| **Shifts** | `ShiftsController`, `ShiftAdminController`, `ShiftDashboardController`, `ShiftWorkloadAdminController`, `VolunteerTrackingController` | — | `ShiftManagementService`, `ShiftSignupService`, `GeneralAvailabilityService`, `VolunteerTrackingService`, `VolunteerTrackingExportService`, `ShiftViewService`, `RotaCoordinatorMessageService`, `BurnSettingsService`, `WorkloadService`, *`CachingShiftViewService`* | `VolunteerTrackingRepository` (+ shift/rota repos) | `rotas`, `shifts`, `shift_signups`, `shift_tags`, `rota_shift_tags`, `event_settings`, `general_availability`, `volunteer_event_profiles`, `volunteer_build_statuses`, `volunteer_tag_preferences`, `event_participations` |
| **Store** | `StoreController`, `StoreAdminController`, `StoreStripeWebhookController` | — | `Service` (`Humans.Store.Services`) | `Repository` / `IStoreRepository` (`Humans.Store.Data`) | `store_products`, `store_orders`, `store_order_lines`, `store_payments`, `store_invoices`, `store_treasury_sync_state` |
| **Surveys** | `SurveyController`, `SurveyAdminController`, `SurveysApiController` | — | `SurveyService` | `SurveyRepository` | `surveys`, `survey_questions`, `survey_question_options`, `survey_invitations`, `survey_responses`, `survey_answers` |
| **Teams** | `TeamController`, `TeamAdminController` | — | `TeamService`, `TeamPageService`, *`CachingTeamService`* | `TeamRepository` | `teams`, `team_members`, `team_join_requests`, `team_join_request_state_history`, `team_role_definitions`, `team_role_assignments` |
| **Tickets** | `TicketController`, `TicketTransferController`, `TicketTransferAdminController`, `TicketsContactsAdminController`, `TicketsOnsiteAdminController` | `OnsiteRosterService`, `TicketingBudgetService` | `TicketQueryService`, `TicketSyncService`, `TicketTransferService`, `AttendeeContactImportService`, *`CachingTicketQueryService`* | `TicketRepository`, `TicketTransferRepository` | `ticket_orders`, `ticket_attendees`, `ticket_sync_state`, `ticket_transfer_requests` |
| **Users / Identity** | `UsersAdminDebugController`, `UnsubscribeController`, `LanguageController` | `AccountDeletionService`, `UserParticipationBackfillService`, `ExternalLoginService` | `UserService`, `AccountProvisioningService`, `UnsubscribeService`, `UserEmailProviderBackfillService`, *`CachingUserService`* | `UserRepository` | `AspNetUsers`, `AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserTokens`, `AspNetRoles` (legacy), `AspNetUserRoles` (legacy) |
| **Onboarding** | `OnboardingReviewController`, `OnboardingWidgetController`, `WelcomeController` | `OnboardingService` | — | — | — |
| **Human Lifecycle** | — (admin actions via `AdminController`) | `HumanLifecycleService` | — | — | — |
| **Early Entry** | `EarlyEntryRosterController` | `EarlyEntryService` | *`CachingEarlyEntryService`* | — | — |
| **Cantina** | `CantinaController` | `CantinaRosterService` | — | — | — (reads Shifts via `IShiftManagementService`) |
| **Dashboard** | — (rendered on Home) | `DashboardService`, `AdminDashboardService` | — | — | — |
| **Search** | `SearchController` | `SearchService` | — | — | — |
| **Mailer** | — (background sync) | `MailerImportService`, `MailerAudienceSyncService` | `MailerLiteClient` | — | — (MailerLite is read-only; writes route through other sections) |
| **Scanner** | `ScannerController` | — | — | — | — (presentational, phase 1) |
| **Debug / Dev** | `DebugController`, `DevSeedController`, `LogApiController`, `ColorPaletteController`, `WidgetGalleryController`, `TimezoneApiController` | — | — | `AdminDatabaseDiagnosticsRepository` | — |

## Cross-cutting concerns

The technical services the business verticals use. Per the hard rules these are horizontal sections and must **not** reference vertical sections.

| Section | Controllers | Orchestrators | Services | Repositories | Tables |
|---------|-------------|---------------|----------|--------------|--------|
| **Audit Log** | `AuditLogController` | `AuditViewerService` | `AuditLogService` | `AuditLogRepository` | `audit_log` |
| **Auth** | `AccountController`, `DevLoginController` | `MagicLinkService` | `RoleAssignmentService`, `AdminAuthorizationService`, *`CachingRoleAssignmentService`* | `RoleAssignmentRepository` | `role_assignments` |
| **Notifications** | `NotificationsController` | — | `NotificationService`, `NotificationInboxService`, `NotificationMeterProvider` | `NotificationRepository` | `notifications`, `notification_recipients` |
| **GDPR** | — (export/delete via `GuestController` / `ProfileController`) | `GdprExportService` | — | — | — |
| **Admin Shell** | `AdminController` (`/Admin` dashboard tile only) | — | `AdminNavTree`, `AdminSidebarViewComponent`, `AdminBreadcrumbViewComponent` (Web layer) | — | — (frame only; owns no tables) |

## Notes & known drift

- **`/Admin/*` is not a vertical section.** `AdminController` is a nav holder; the actions it exposes belong to their owning sections (outbox pause → Email, suspend/merge/purge → Profiles/Users, sync settings → Google Integration, role assignments → Auth, legal-doc management → Consent). It *is* listed above as the cross-cutting **Admin Shell** — the logical holder for the admin-type bits each section contributes ([`admin-shell.md`](admin-shell.md) documents the shared frame: sidebar, breadcrumb, dashboard skeleton). Long-term direction is for the shell to become framework plumbing with a dynamically built nav, so keep the row: it is the seam that work lands on, not a vertical section sneaking in.
- **`design-rules.md` §8 divergences (code wins):** §8 names Event Guide's service `EventGuideService`, but the class is `EventService`. §8's Camps row omits `CampRoleService` and the `camp_role_*` tables. §8 lists `google_resources` under Teams, but `TeamResourceService` + `GoogleResourceRepository` now live in Google Integration. §8's Finance tables list (`holded_transactions`) does not match the current `holded_*` tables. These are drift to reconcile in §8.
- **`event_participations`** is configured under the Shifts configuration folder and read by Shifts; §8 attributes it to Users/Identity. Confirm the owning repository before relying on either.
