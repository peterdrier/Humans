# Authorization Inventory

Originally produced as Phase 0 of the [first-class authorization transition plan](plans/2026-04-03-first-class-authorization-transition.md) (kept linked for historical context). **Phase 1 is complete:** every canonical policy in §5 is registered in `AuthorizationPolicyExtensions.AddHumansAuthorizationPolicies`, all controllers (including the Events Guide section, which now uses `[Authorize(Policy = PolicyNames.EventsAdminOrAdmin)]`) use `[Authorize(Policy = PolicyNames.X)]`, the `authorize-policy` TagHelper resolves through `IAuthorizationService`, and views no longer call `RoleChecks.*` / `ShiftRoleChecks.*` directly. **Phase 2 (resource-based authorization)** has shipped multiple vertical slices — see §6 (`TeamAuthorizationHandler`, `CampAuthorizationHandler`, `BudgetAuthorizationHandler`, `RoleAssignmentAuthorizationHandler`, `ContainerAuthorizationHandler`, `ExpenseReportAuthorizationHandler`, `IbanAccessHandler`, `StoreOrderAuthorizationHandler`, `UserEmailAuthorizationHandler`, `IssuesAuthorizationHandler`, `AgentRateLimitHandler`). **Phase 3 (service-layer enforcement) is cancelled** — see the tombstone in the transition plan.

Generated 2026-04-03. Refreshed 2026-05-28 (full re-scan via `/freshness-sweep`). Covers every `[Authorize(Policy)]` / `[Authorize(Roles)]` attribute on controllers and actions in `src/Humans.Web/Controllers/` (including `Controllers/Api/` and `Controllers/Mailer/`), every `RoleChecks.*` / `ShiftRoleChecks.*` invocation across `src/Humans.Web/` and `src/Humans.Application/`, every `IAuthorizationService.AuthorizeAsync` call site, every `authorize-policy` TagHelper attribute and `User.IsInRole` / `Model.X` authorization check across `src/Humans.Web/Views/` and `src/Humans.Web/ViewComponents/`, and every `AuthorizationHandler<T, R>` (and `IAuthorizationHandler`) under `src/Humans.Web/Authorization/` and `src/Humans.Application/Authorization/`.

The `Source` column reflects the constant referenced in the attribute as it appears in the code today.

---

## 1. Controller Authorization Map

### Admin Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `AdminController` | Class | `[Route("Admin")]` only — no class-level `[Authorize]` | — |
| `AdminController.Index` | Action | `Admin, Board, HumanAdmin, TeamsAdmin, CampAdmin, TicketAdmin, EventsAdmin, FeedbackAdmin, FinanceAdmin, StoreAdmin, CantinaAdmin, NoInfoAdmin, VolunteerCoordinator, ConsentCoordinator` | `PolicyNames.AnyAdminRole` |
| `AdminController.PurgeHuman` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `AdminController.Logs` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `AdminController.Maintenance` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `AdminController.Configuration` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `AdminController.DbVersion` | Action | `AllowAnonymous` | Override |
| `AdminController.DbStats` / `ResetDbStats` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `AdminController.ClearHangfireLocks` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `AdminController.BackfillUserEmailProviders` / `BackfillUserEmailProvidersRun` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `AdminController.CacheStats` / `ResetCacheStats` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `AdminController.AudienceSegmentation` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `AdminAgentController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `AdminDuplicateAccountsController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `AdminMergeController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `AdminLegalDocumentsController` | Class | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `EmailController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `MailerAdminController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `ProfileAdminController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `ProfileBackfillAdminController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `ProfilePictureMigrationAdminController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `UsersAdminDebugController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `WidgetGalleryController` | Class | `Admin` | `PolicyNames.AdminOnly` |

### Google Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `GoogleController` | Class | `[Route("Google")]` only — no class-level `[Authorize]` | — |
| `GoogleController.SyncSettings` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.UpdateSyncSetting` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.SyncSystemTeams` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.SyncResults` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.CheckGroupSettings` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.GroupSettingsResults` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.RemediateGroupSettings` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.RemediateAllGroupSettings` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.AllGroups` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.LinkGroupToTeam` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.Sync` | Action | `TeamsAdmin, Board, Admin` | `PolicyNames.TeamsAdminBoardOrAdmin` |
| `GoogleController.SyncPreview` | Action | `TeamsAdmin, Board, Admin` | `PolicyNames.TeamsAdminBoardOrAdmin` |
| `GoogleController.SyncExecute` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.SyncExecuteAll` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.ProvisionEmail` | Action | `HumanAdmin, Admin` | `PolicyNames.HumanAdminOrAdmin` |
| `GoogleController.Accounts` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.ProvisionAccount` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.SuspendAccount` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.ReactivateAccount` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.ResetPassword` / `ResetPasswordAndGenerate2Fa` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.LinkAccount` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.SyncOutbox` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.CheckEmailRenames` / `EmailRenames` / `EmailFlagViolations` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.Index` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `AuditLogController.Index` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `AuditLogController.CheckDriveActivity` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `AuditLogController.Resource` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `AuditLogController.Human` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |

### Tickets Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `TicketController` | Class | `TicketAdmin, Admin, Board` | `PolicyNames.TicketAdminBoardOrAdmin` |
| `TicketController.Sync` | Action | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `TicketController.FullResync` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `TicketController.ParticipationBackfill` (GET/POST) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `TicketController.ExportAttendees` | Action | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `TicketController.ExportOrders` | Action | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `TicketTransferController` | Class | `[Authorize]` (authenticated) | — |
| `TicketTransferAdminController` | Class | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `TicketsContactsAdminController` | Class | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `TicketsOnsiteAdminController` | Class | `TicketAdmin, Admin, Board` | `PolicyNames.TicketAdminBoardOrAdmin` |

### Scanner Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ScannerController` | Class | `TicketAdmin, Admin, Board` | `PolicyNames.TicketAdminBoardOrAdmin` |

### Campaigns Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CampaignController` | Class | `[Authorize]` (authenticated) | — |
| `CampaignController.Index` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.Create` (GET/POST) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.Edit` (GET/POST) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.Detail` | Action | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `CampaignController.ImportCodes` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.GenerateCodes` | Action | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `CampaignController.Activate` / `Complete` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.SendWave` (GET/POST) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.Resend` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampaignController.RetryAllFailed` | Action | `Admin` | `PolicyNames.AdminOnly` |

### Finance Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `FinanceController` | Class | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` |

### Budget Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `BudgetController` | Class | `[Authorize]` (authenticated) | — |
| Runtime guards | In-method | `_authService.AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` and `_authService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)` | Resource-based (see §6) |

### Expenses Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ExpensesController` | Class | `[Authorize]` (authenticated) | — |
| `ExpensesController.Review` | Action | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` |
| `ExpensesController.Approve` | Action | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` |
| `ExpensesController.Reject` | Action | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` |
| `ExpensesController.SepaGenerate` | Action | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` |
| `ExpensesController` runtime guards | In-method | `_authService.AuthorizeAsync(User, report, ExpenseReportOperationRequirement.*)` | Resource-based (see §6) |

### Store Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `StoreController` | Class | `[Authorize]` (authenticated) | — |
| `StoreController` runtime guards | In-method | `_authService.AuthorizeAsync(User, order, StoreOrderOperationRequirement.{View, AddLine, RemoveLine, EditCounterparty, Pay, Delete})` and `StoreOrderCreateContext` for Create | Resource-based (see §6) |
| `StoreAdminController` | Class | `StoreAdmin, FinanceAdmin, Admin` | `PolicyNames.StoreCatalogAdmin` |
| `StoreStripeWebhookController` | Class | `AllowAnonymous` (Stripe signature-verified) | — |

### Board Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| (No standalone `BoardController` — board-only actions live under `GovernanceBoardVotingController` below.) | | | |

### Onboarding Review Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `OnboardingReviewController` | Class | `ConsentCoordinator, VolunteerCoordinator, Board, Admin` | `PolicyNames.ReviewQueueAccess` |
| `OnboardingReviewController.Clear` | Action | `ConsentCoordinator, Board, Admin` | `PolicyNames.ConsentCoordinatorBoardOrAdmin` |
| `OnboardingReviewController.BulkClear` | Action | `ConsentCoordinator, Board, Admin` | `PolicyNames.ConsentCoordinatorBoardOrAdmin` |
| `OnboardingReviewController.Flag` | Action | `ConsentCoordinator, Board, Admin` | `PolicyNames.ConsentCoordinatorBoardOrAdmin` |
| `OnboardingReviewController.Reject` | Action | `ConsentCoordinator, Board, Admin` | `PolicyNames.ConsentCoordinatorBoardOrAdmin` |
| `OnboardingWidgetController` | Class | `[Authorize]` (authenticated) | — |

### Governance / Application Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `GovernanceController` | Class | `[Authorize]` (authenticated) | — |
| `GovernanceApplicationsController` | Class | `[Authorize]` (authenticated) | — |
| `GovernanceApplicationsController.Admin` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `GovernanceApplicationsController.AdminDetail` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `GovernanceBoardVotingController` | Class | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `GovernanceBoardVotingController.Vote` | Action | `Board` | `PolicyNames.BoardOnly` |

### Profile / Contacts Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ProfileController` | Class | `[Authorize]` (authenticated) | — |
| `ProfileController.VerifyEmail` | Action | `AllowAnonymous` | Override |
| `ProfileController.Picture` | Action | `AllowAnonymous` | Override |
| `ProfileController.PublicPopover` | Action | `AllowAnonymous` | Override (`[HttpGet("{id:guid}/PublicPopover")]`; 404s unless target is a coordinator on a public-page team) |
| `ProfileController.AdminAddVerifiedEmail` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `ProfileController.AdminVerifyEmail` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `ProfileController.AdminList` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.Roles` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.AdminDetail` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.RevealIban` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `ProfileController.AdminOutbox` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.SuspendHuman` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.UnsuspendHuman` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.ApproveVolunteer` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.RejectSignup` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.AddRole` (GET/POST) | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.EndRole` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.AddRole/EndRole` runtime guards | In-method | `_authorizationService.AuthorizeAsync(User, roleName, RoleAssignmentOperationRequirement.Manage)` | Resource-based (see §6) |
| `ProfileController` email-action runtime guards | In-method | `_authorizationService.AuthorizeAsync(User, userId, UserEmailOperations.Edit)` (gating 18 email-edit endpoints) | Resource-based (see §6) |
| `ProfileApiController` | Class | `[Authorize]` (authenticated) | — |

### Teams Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `TeamController` | Class | `[Authorize]` (authenticated) | — |
| `TeamController.Index` | Action | `AllowAnonymous` | Override |
| `TeamController.Details` | Action | `AllowAnonymous` | Override |
| `TeamController.Summary` | Action | `TeamsAdmin, Board, Admin` | `PolicyNames.TeamsAdminBoardOrAdmin` |
| `TeamController.CreateTeam` (GET/POST) | Action | `TeamsAdmin, Board, Admin` | `PolicyNames.TeamsAdminBoardOrAdmin` |
| `TeamController.EditTeam` (GET/POST) | Action | `TeamsAdmin, Board, Admin` | `PolicyNames.TeamsAdminBoardOrAdmin` |
| `TeamController.DeleteTeam` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `TeamController.GetTeamGoogleResources` | Action | `TeamsAdmin, Board, Admin` | `PolicyNames.TeamsAdminBoardOrAdmin` |
| `TeamAdminController` | Class | `[Authorize]` (authenticated) | Coordinator checks at runtime via `HumansTeamControllerBase` |
| `TeamAdminController` runtime guards | In-method | `_authorizationService.AuthorizeAsync(User, team, TeamOperationRequirement.ManageCoordinators)` | Resource-based (see §6) |

### Camps Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CampController` | Class | None at class level — anonymous public actions + `[Authorize]` per action | Camp lead + CampAdmin runtime checks |
| `CampController.Index` / `Details` / `SeasonDetails` | Action | `AllowAnonymous` | Override |
| `CampController.*` (Contact/Edit/Register/Members/Withdraw/Rejoin/AssignRole/UploadImage/etc.) | Action | `[Authorize]` (authenticated) | — |
| `CampController` runtime guards | In-method | `_authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` via `HumansCampControllerBase` | Resource-based (see §6) |
| `CampAdminController` | Class | `CampAdmin, Admin` | `PolicyNames.CampAdminOrAdmin` |
| `CampAdminController.Delete` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampApiController` | Class | `AllowAnonymous` (with `BarriosPublic` CORS) | — |
| `ContainerController` | Class | `[Authorize]` (authenticated) | — |
| `ContainerController` runtime guards | In-method | `_authorizationService.AuthorizeAsync(User, target, ContainerOperationRequirement.{Manage, Place})` | Resource-based (see §6) |

### Shifts Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ShiftsController` | Class | `[Authorize]` (authenticated) | — |
| `ShiftsController.Settings` (GET/POST) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `ShiftsController.OrphanSignups` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `ShiftAdminController` | Class | `[Authorize]` (authenticated) | Coordinator checks at runtime via `HumansTeamControllerBase` |
| `ShiftAdminController.MoveRota` | Action | `Admin, VolunteerCoordinator` | `PolicyNames.VolunteerManager` |
| `ShiftDashboardController` | Class | `Admin, NoInfoAdmin, VolunteerCoordinator` (role requirement) OR any team manager/coordinator (custom handler) | `PolicyNames.ShiftDepartmentManager` |
| `ShiftDashboardController.SearchVolunteers` | Action | `Admin, NoInfoAdmin, VolunteerCoordinator` | `PolicyNames.ShiftDashboardAccess` |
| `ShiftDashboardController.Voluntell` | Action | `Admin, NoInfoAdmin, VolunteerCoordinator` | `PolicyNames.ShiftDashboardAccess` |
| `ShiftWorkloadAdminController` | Class | `Admin, NoInfoAdmin, VolunteerCoordinator` | `PolicyNames.ShiftDashboardAccess` |
| `EarlyEntryRosterController` | Class | `Admin, NoInfoAdmin, VolunteerCoordinator` | `PolicyNames.ShiftDashboardAccess` |
| `VolunteerTrackingController` | Class | `Admin, NoInfoAdmin, VolunteerCoordinator` | `PolicyNames.ShiftDashboardAccess` |
| `VolunteerTrackingController.SetCampSetup` / `ClearCampSetup` / `SetDayOff` / `ClearDayOff` | Action | `Admin, VolunteerCoordinator` | `PolicyNames.VolunteerTrackingWrite` |

### Events Guide Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `EventsController` | Class | `[Authorize]` (authenticated) + `[ServiceFilter(typeof(EventsFeatureFilter))]` | — |
| `EventsController` barrio-event runtime guards | In-method | `_authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.SubmitEvent)` via `HumansCampControllerBase.ResolveCampEventManagementAsync` | Resource-based (see §6) |
| `EventsAdminController` | Class | `EventsAdmin, Admin` | `PolicyNames.EventsAdminOrAdmin` |
| `EventsDashboardController` | Class | `EventsAdmin, Admin` | `PolicyNames.EventsAdminOrAdmin` |
| `EventsExportController` | Class | `EventsAdmin, Admin` | `PolicyNames.EventsAdminOrAdmin` |
| `EventsModerationController` | Class | `EventsAdmin, Admin` | `PolicyNames.EventsAdminOrAdmin` |
| `EventsApiController` | Class | `[ApiController]`, `[EnableCors("EventsApi")]`, `[ServiceFilter(typeof(EventsFeatureFilter))]` — no class-level `[Authorize]` | — |
| `EventsApiController.GetEvents/GetEvent/GetBarrios/GetBarrio/GetCategories` | Action | (anonymous reads) | — |
| `EventsApiController.GetPreferences/UpdatePreferences/GetFavourites/AddFavourite/RemoveFavourite` | Action | `[Authorize]` (authenticated) | — |

### Cantina Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CantinaController` | Class | `CantinaAdmin, Admin` | `PolicyNames.CantinaAdminOrAdmin` |

### Calendar Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CalendarController` | Class | `[Authorize]` (authenticated) | — |

### City Planning Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CityPlanningController` | Class | `[Authorize]` (authenticated) | — |
| `CityPlanningController` runtime guards | In-method | `RoleChecks.IsCampAdmin(User)` and lead-of-camp checks | RoleChecks helper |
| `CityPlanningApiController` | Class | `[Authorize]` (authenticated) | — |
| `CityPlanningApiController` runtime guards | In-method | `RoleChecks.IsCampAdmin(User)` and lead-of-camp checks; `_authorizationService.AuthorizeAsync(...)` on three endpoints | RoleChecks helper + resource-based |

### Feedback Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `FeedbackController` | Class | `[Authorize]` (authenticated) | — |
| `FeedbackController.UpdateStatus` | Action | `FeedbackAdmin, Admin` | `PolicyNames.FeedbackAdminOrAdmin` |
| `FeedbackController.UpdateAssignment` | Action | `FeedbackAdmin, Admin` | `PolicyNames.FeedbackAdminOrAdmin` |
| `FeedbackController.SetGitHubIssue` | Action | `FeedbackAdmin, Admin` | `PolicyNames.FeedbackAdminOrAdmin` |
| `FeedbackController` runtime guards | In-method | `RoleChecks.IsFeedbackAdmin(User)` to drive admin-vs-user view | RoleChecks helper |
| `FeedbackApiController` | Class | `[ServiceFilter(typeof(ApiKeyAuthFilter))]` (API-key auth) | `ApiKeyAuthFilter` |

### Issues Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `IssuesController` | Class | `[Authorize]` (authenticated) | — |
| `IssuesController` runtime guards | In-method | `_authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)` on every mutating endpoint | Resource-based (see §6) |
| `IssuesApiController` | Class | `[ServiceFilter(typeof(IssuesApiKeyAuthFilter))]` (API-key auth) | `IssuesApiKeyAuthFilter` |

### Agent Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `AgentController` | Class | `[Authorize]` (authenticated) | — |
| `AgentController.Ask` | In-method | `_auth.AuthorizeAsync(User, user.Id, PolicyNames.AgentRateLimit)` | Resource-based (see §6) |
| `AgentApiController` | Class | `[ServiceFilter(typeof(AgentApiKeyAuthFilter))]` (API-key auth) | `AgentApiKeyAuthFilter` |

### Guide Section (Help Documentation)

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `GuideController` | Class | (no class-level `[Authorize]`) | — |
| `GuideController.Index` | Action | `AllowAnonymous` | Override |
| `GuideController.Document` | Action | `AllowAnonymous` | Override |
| `GuideController.Refresh` | Action | `Admin` | `PolicyNames.AdminOnly` |

### Debug Section (Developer Diagnostics)

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `DebugController` | Class | `Admin` | `PolicyNames.AdminOnly` |

### Search Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `SearchController` | Class | `[Authorize]` (authenticated) | — |

### Notifications

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `NotificationsController` | Class | `[Authorize]` (authenticated) | — |

### About / Home / Account / Misc

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `AboutController` | Class | (no class-level `[Authorize]`) | — |
| `AboutController.Staff` | Action | `[Authorize]` (authenticated) | — |
| `HomeController` | Class | (no class-level `[Authorize]`) | — |
| `HomeController.DeclareNotAttending` | Action | `[Authorize]` (authenticated) | — |
| `HomeController.UndoNotAttending` | Action | `[Authorize]` (authenticated) | — |
| `AccountController` | Class | (no class-level `[Authorize]`) | — |
| `UnsubscribeController` | Class | (no class-level `[Authorize]`) | — |
| `LanguageController` | Class | (no class-level `[Authorize]`) | — |
| `DevLoginController` | Class | (no class-level `[Authorize]`) | — |
| `WelcomeController` | Class | `AllowAnonymous` | — |
| `ColorPaletteController` | Class | `AllowAnonymous` | — |

### Dev Seed (test data)

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `DevSeedController` | Class | `[Authorize]` (authenticated) | — |
| `DevSeedController.SeedBudget` | Action | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` |
| `DevSeedController.SeedCampRoles` | Action | `CampAdmin, Admin` | `PolicyNames.CampAdminOrAdmin` |
| `DevSeedController.SeedDashboard` | Action | `Admin, NoInfoAdmin, VolunteerCoordinator` | `PolicyNames.ShiftDashboardAccess` |
| `DevSeedController.ResetDashboard` | Action | `Admin` | `[Authorize(Roles = RoleNames.Admin)]` |

### Guest / Consent

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `GuestController` | Class | `[Authorize]` (authenticated) | — |
| `GuestController.CommunicationPreferences` (GET/POST) | Action | `AllowAnonymous` (token-validated) | Override (see WARNING in source) |
| `GuestController.UpdatePreference` | Action | `AllowAnonymous` (token-validated) | Override |
| `ConsentController` | Class | `[Authorize]` (authenticated) | — |

### Public / API

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `LegalController` | Class | `AllowAnonymous` | — |
| `LogApiController` | Class | `[ServiceFilter(typeof(LogApiKeyAuthFilter))]` (API-key auth) | `LogApiKeyAuthFilter` |
| `TimezoneApiController` | Class | (no class-level `[Authorize]`) | — |
| `HangfireAuthorizationFilter` | Filter | `RoleChecks.IsAdmin(User)` | Admin only |

---

## 2. View Authorization Map

Views express authorization four ways today:

1. **`authorize-policy="PolicyName"` TagHelper attribute** — the dominant pattern. Resolves through `IAuthorizationService.AuthorizeAsync(User, policyName)` via `AuthorizeViewTagHelper`. Hides the element when the policy fails.
2. **`(await AuthService.AuthorizeAsync(User, PolicyNames.X)).Succeeded`** — used when a view needs the boolean for branching, multi-use within the page, or to drive a `var` flag rather than gate one element. Requires `@inject IAuthorizationService AuthService`.
3. **`User.IsInRole(RoleNames.X)` direct calls** — no longer present in any view file (all build-hash, Events-dropdown, Guide-layout, and Profile/AdminDetail call sites have been migrated to `AuthService.AuthorizeAsync` flag variables or `authorize-policy` attributes — verified 2026-05-28).
4. **`Model.CanX` / `Model.IsX` view-model properties** — for resource-relative checks (coordinator-of-this-team, lead-of-this-camp, can-edit-this-budget) and for status-driven UI (suspended badge, approved badge, etc.). The view does not know about roles; the controller / view-model author resolved authorization upstream.

`RoleChecks.*` and `ShiftRoleChecks.*` are no longer invoked from any view file (Phase 1 retirement complete — verified 2026-05-28).

### Nav Layout (`_Layout.cshtml`)

| Line | Check | Controls |
|---|---|---|
| 36 | `var isAnyAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.AnyAdminRole)).Succeeded` | Drives `isAnyAdmin` flag for build-hash tooltip on brand link (commit SHA on hover) |
| 37 | `var isEventsAdminOrAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.EventsAdminOrAdmin)).Succeeded` | Drives `isEventsAdminOrAdmin` flag for the Events admin sub-dropdowns below |
| 97 | `authorize-policy="IsActiveMember"` | City Planning nav link |
| 102 | `authorize-policy="IsActiveMember"` | Events dropdown (feature-flagged) |
| 108 | `if (isEventsAdminOrAdmin)` | Guide Dashboard / Moderate / Export dropdown items |
| 115 | `if (isEventsAdminOrAdmin)` | Guide Settings / Categories / Venues dropdown items |
| 131 | `authorize-policy="ActiveMemberOrShiftAccess"` | Shifts nav link |
| 134 | `authorize-policy="IsActiveMember"` | Budget nav link |
| 137 | `authorize-policy="AnyAdminRole"` | Admin nav link (entry to admin shell) |

### Login Partial (`_LoginPartial.cshtml`)

| Line | Check | Controls |
|---|---|---|
| 50 | `authorize-policy="IsActiveMember"` | Governance link in profile dropdown |

### Guide Layout (`_GuideLayout.cshtml`)

| Line | Check | Controls |
|---|---|---|
| 40 | `authorize-policy="AdminOnly"` | "Refresh from GitHub" button |

### Shift Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Shifts/Index.cshtml` | 63 | `authorize-policy="ShiftDepartmentManager"` | Dashboard button |
| `Shifts/Index.cshtml` | 64 | `authorize-policy="AdminOnly"` | Settings button |
| `Shifts/NoActiveEvent.cshtml` | 8 | `authorize-policy="AdminOnly"` | "Configure Event Settings" link |
| `ShiftDashboard/Index.cshtml` | 87 | `authorize-policy="ShiftDashboardAccess"` | Voluntell card |
| `ShiftDashboard/Index.cshtml` | 228 | `authorize-policy="ShiftDashboardAccess"` | Volunteer search column |
| `ShiftDashboard/Index.cshtml` | 317, 327 | `authorize-policy="ShiftDashboardAccess"` | Per-row signup-action cells |
| `VolunteerTracking/Index.cshtml` | 8 | `(await AuthService.AuthorizeAsync(User, PolicyNames.VolunteerTrackingWrite)).Succeeded` | Drives `canWrite` flag for write controls below |
| `VolunteerTracking/_VolunteerHeatmap.cshtml` | 9 | `(await AuthService.AuthorizeAsync(User, PolicyNames.VolunteerTrackingWrite)).Succeeded` | Drives `canWrite` flag for cell-level write actions |
| `VolunteerTracking/_VolunteerUnbookedHeatmap.cshtml` | 9 | `(await AuthService.AuthorizeAsync(User, PolicyNames.VolunteerTrackingWrite)).Succeeded` | Drives `canWrite` flag for cell-level write actions |

### Profile Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Profile/Index.cshtml` | 15 | `authorize-policy="HumanAdminBoardOrAdmin"` | "Admin" link to AdminDetail |
| `Profile/Index.cshtml` | 71 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminBoardOrAdmin)).Succeeded` | `ProfileCardViewMode.Admin` vs `Public` for non-own profiles |
| `Profile/Emails.cshtml` | 17 | `(await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Admin-only email management controls |
| `Profile/AdminDetail.cshtml` | 10 | `var isAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Drives `isAdmin` flag for the two Admin-only data blocks at lines 301 and 348 |

### Board / Onboarding Review Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Governance/BoardVoting/Detail.cshtml` | 117 | `authorize-policy="BoardOnly"` | Vote casting card |

### Team Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Team/Index.cshtml` | 20 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminBoardOrAdmin)).Succeeded` | "Summary" + "Sync Status" toolbar buttons on the Teams landing page |
| `Team/Summary.cshtml` | 22 | `authorize-policy="BoardOrAdmin"` | "Create Team" button |
| `Team/Summary.cshtml` | 51 | `authorize-policy="BoardOrAdmin"` | Actions column header |
| `Team/_AdminTeamRow.cshtml` | 44 | `(await AuthService.AuthorizeAsync(User, PolicyNames.BoardOrAdmin)).Succeeded` | Pending-shift-signup badge link |
| `Team/_AdminTeamRow.cshtml` | 96 | `authorize-policy="BoardOrAdmin"` | Actions column cell (Edit/Deactivate buttons) |
| `Team/EditTeam.cshtml` | 81 | `authorize-policy="AdminOnly"` | "Sensitive team" checkbox |

### Camp Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Camp/Index.cshtml` | 11 | `authorize-policy="CampAdminOrAdmin"` | "Camp Admin" link |
| `CampAdmin/Index.cshtml` | 472 | `authorize-policy="AdminOnly"` | Danger Zone card (Delete Camp) |

### Ticket Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Ticket/Index.cshtml` | 280 | `authorize-policy="TicketAdminOrAdmin"` | "Sync Now" form |
| `Ticket/Index.cshtml` | 286 | `authorize-policy="AdminOnly"` | "Full Re-sync" form |
| `Ticket/Index.cshtml` | 294 | `authorize-policy="TicketAdminOrAdmin"` | Export link |
| `Ticket/_TicketNav.cshtml` | 26 | `authorize-policy="AdminOnly"` | "Backfill" tab |

### Google Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Google/_SyncTabContent.cshtml` | 8 | `(await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Drives `canExecuteActions` flag for execute-action buttons on the Google sync tab |
| `Google/_SyncTabContent.cshtml` | 9 | `(await AuthService.AuthorizeAsync(User, PolicyNames.BoardOrAdmin)).Succeeded` | Drives `canViewAudit` flag for the audit-log link on the Google sync tab |

### Campaign Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Campaign/Detail.cshtml` | 21 | `var isAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Drives admin-gated buttons below |
| `Campaign/Detail.cshtml` | 22 | `var canGenerateCodes = (await AuthService.AuthorizeAsync(User, PolicyNames.TicketAdminOrAdmin)).Succeeded` | Drives "Generate Codes" form |

### Shared Components

| View | Line | Check | Controls |
|---|---|---|---|
| `Shared/Components/ProfileCard/Default.cshtml` | 29 | `(await AuthService.AuthorizeAsync(User, PolicyNames.HumanAdminBoardOrAdmin)).Succeeded` | Admin / Board view of profile card |
| `Shared/_HumanPopover.cshtml` | 7 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminBoardOrAdmin)).Succeeded` | Admin popover details |
| `Shared/_HumanPopover.cshtml` | 17 | `(await AuthService.AuthorizeAsync(User, PolicyNames.HumanAdminBoardOrAdmin)).Succeeded` | HumanAdmin/Board/Admin popover quick actions |
| `WidgetGallery/Index.cshtml` | 1018 / 1023 | `authorize-policy="@PolicyNames.AdminOnly"` / `authorize-policy="DefinitelyNotARealPolicyName"` | Documentation/demo of the TagHelper (not production gating) |
| `AuthorizeViewTagHelper` | — | `IAuthorizationService.AuthorizeAsync(user, Policy)` | Backs every `authorize-policy="..."` attribute above |
| `AdminSidebarViewComponent` | line 31 | `IAuthorizationService.AuthorizeAsync(HttpContext.User, null, item.Policy)` | Filters /Admin sidebar items per policy |

---

## 3. Same-Rule-Different-Spelling Table

Post Phase-1 retirement, controllers and views express the same authorization rule by referencing the same `PolicyNames` constant — the controller via the `[Authorize(Policy = ...)]` attribute, the view via the `authorize-policy="..."` TagHelper attribute (or `(await AuthService.AuthorizeAsync(User, PolicyNames.X)).Succeeded` when a boolean is needed). The legacy `RoleChecks.*` / `ShiftRoleChecks.*` helpers are no longer invoked from any view, and the Events Guide section's controllers and `_Layout.cshtml` dropdown both resolve through `PolicyNames.EventsAdminOrAdmin`.

| Rule | Controller Spelling | View Spelling |
|---|---|---|
| Admin only | `[Authorize(Policy = PolicyNames.AdminOnly)]` | `authorize-policy="AdminOnly"` |
| Any admin role (admin shell) | `[Authorize(Policy = PolicyNames.AnyAdminRole)]` | `authorize-policy="AnyAdminRole"` |
| Board or Admin | `[Authorize(Policy = PolicyNames.BoardOrAdmin)]` | `authorize-policy="BoardOrAdmin"` |
| TeamsAdmin/Board/Admin | `[Authorize(Policy = PolicyNames.TeamsAdminBoardOrAdmin)]` | `authorize-policy="TeamsAdminBoardOrAdmin"` |
| TicketAdmin/Board/Admin | `[Authorize(Policy = PolicyNames.TicketAdminBoardOrAdmin)]` | `authorize-policy="TicketAdminBoardOrAdmin"` |
| TicketAdmin or Admin | `[Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]` | `authorize-policy="TicketAdminOrAdmin"` |
| CampAdmin or Admin | `[Authorize(Policy = PolicyNames.CampAdminOrAdmin)]` | `authorize-policy="CampAdminOrAdmin"` |
| HumanAdmin/Board/Admin | `[Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]` | `authorize-policy="HumanAdminBoardOrAdmin"` |
| FeedbackAdmin or Admin | `[Authorize(Policy = PolicyNames.FeedbackAdminOrAdmin)]` | `authorize-policy="FeedbackAdminOrAdmin"` |
| FinanceAdmin or Admin | `[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]` | `authorize-policy="FinanceAdminOrAdmin"` |
| CantinaAdmin or Admin | `[Authorize(Policy = PolicyNames.CantinaAdminOrAdmin)]` | `authorize-policy="CantinaAdminOrAdmin"` |
| Store catalog admin | `[Authorize(Policy = PolicyNames.StoreCatalogAdmin)]` | (no view spelling — controller-only today) |
| EventsAdmin or Admin | `[Authorize(Policy = PolicyNames.EventsAdminOrAdmin)]` | `(await AuthService.AuthorizeAsync(User, PolicyNames.EventsAdminOrAdmin)).Succeeded` |
| Review queue access | `[Authorize(Policy = PolicyNames.ReviewQueueAccess)]` | (no current view spelling) |
| Consent coordinator + B/A | `[Authorize(Policy = PolicyNames.ConsentCoordinatorBoardOrAdmin)]` | (no current view spelling) |
| Board only | `[Authorize(Policy = PolicyNames.BoardOnly)]` | `authorize-policy="BoardOnly"` |
| Shift dashboard access | `[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]` | `authorize-policy="ShiftDashboardAccess"` |
| Shift department manager | `[Authorize(Policy = PolicyNames.ShiftDepartmentManager)]` | `authorize-policy="ShiftDepartmentManager"` |
| Volunteer tracking write | `[Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]` | `(await AuthService.AuthorizeAsync(User, PolicyNames.VolunteerTrackingWrite)).Succeeded` |
| Active member or shift access | `[Authorize(Policy = PolicyNames.ActiveMemberOrShiftAccess)]` | `authorize-policy="ActiveMemberOrShiftAccess"` |
| Active member | `[Authorize(Policy = PolicyNames.IsActiveMember)]` | `authorize-policy="IsActiveMember"` |
| Resource: team coord/admin | `_authorizationService.AuthorizeAsync(User, team, TeamOperationRequirement.ManageCoordinators)` | `Model.IsCurrentUserCoordinator` (view-model) |
| Resource: camp lead/admin | `_authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` | `Model.IsCurrentUserLead \|\| Model.IsCurrentUserCampAdmin` (view-model) |
| Resource: camp-event submit | `_authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.SubmitEvent)` | (no view spelling — controller-only) |
| Resource: budget edit | `_authorizationService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)` | `Model.CanEdit` (view-model) |
| Resource: container place/manage | `_authorizationService.AuthorizeAsync(User, target, ContainerOperationRequirement.{Manage, Place})` | `Model.CanX` (view-model) |
| Resource: store order | `_authService.AuthorizeAsync(User, order, StoreOrderOperationRequirement.{View, AddLine, RemoveLine, EditCounterparty, Pay})` | `Model.CanX` (view-model) |
| Resource: expense report | `_authService.AuthorizeAsync(User, report, ExpenseReportOperationRequirement.X)` | `Model.CanX` (view-model) |
| Resource: IBAN access | `_authService.AuthorizeAsync(User, requirement)` (IbanAccessRequirement) | `Model.CanRevealIban` (view-model) |
| Resource: issue handle | `_authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)` | `Model.CanHandle` (view-model) |
| Resource: user-email edit | `_authorizationService.AuthorizeAsync(User, userId, UserEmailOperations.Edit)` | (no view spelling) |
| Resource: agent rate-limit | `_auth.AuthorizeAsync(User, user.Id, PolicyNames.AgentRateLimit)` | (no view spelling) |
| Resource: role assignment | `_authorizationService.AuthorizeAsync(User, roleName, RoleAssignmentOperationRequirement.Manage)` | (UI list driven by `IRoleAssignmentService.GetAssignableRolesAsync`) |

---

## 4. Enforcement Gaps

### View-Only (button hidden, no server-side attribute guard)

| Location | Check | Risk |
|---|---|---|
| `CampAdmin/Index.cshtml` — "Delete Camp" | `authorize-policy="AdminOnly"` in view | Delete action has `[Authorize(Policy = PolicyNames.AdminOnly)]` — **OK, narrower than class-level CampAdminOrAdmin**. |
| `Team/Summary.cshtml` / `_AdminTeamRow.cshtml` — Edit/Delete/Archive links | `authorize-policy="BoardOrAdmin"` in view | Team edit actions have `[Authorize(Policy = PolicyNames.TeamsAdminBoardOrAdmin)]` — view is **stricter** than server (hides from TeamsAdmin). |
| `Ticket/_TicketNav.cshtml` — Backfill / Settings links | `authorize-policy="AdminOnly"` in view | Targets `Shifts/Settings` / Ticket admin actions which have `[Authorize(Policy = PolicyNames.AdminOnly)]` — **OK**. |

### Server-Only (protected endpoint, no visible UI gating)

| Endpoint | Roles | Note |
|---|---|---|
| `GoogleController` actions with broader policies (`Sync`, `SyncPreview`, `CheckDriveActivity`, `AuditLog/Resource`, `AuditLog/Human`, `ProvisionEmail`) | TeamsAdmin/Board/Admin / Board/Admin / HumanAdmin/Board/Admin / HumanAdmin/Admin | Class-level `[Authorize]` was removed; each action has its own policy. |
| `ProfileController.AdminOutbox` | `HumanAdminBoardOrAdmin` | No visible button in `AdminList` view (accessed via URL pattern). |

### Runtime-Only Guards (no attribute, enforced in method body)

These actions rely on `if` checks + early return/forbid instead of `[Authorize(Policy)]`:

| Controller | Action | Guard |
|---|---|---|
| `ShiftAdminController` | All non-public actions | Coordinator-of-team check via `HumansTeamControllerBase.ResolveTeamManagementAsync` (resource-based) |
| `TeamAdminController` | All non-public actions | Coordinator-of-team check via `HumansTeamControllerBase.ResolveTeamManagementAsync`; `RoleChecks.IsTeamsAdmin(User)` / `RoleChecks.IsAdmin(User)` toggle management features |
| `BudgetController` | `Index`, `Summary`, `CategoryDetail`, line-item CRUD | `_authService.AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` and `_authService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)` |
| `CampController` | All management actions | `_authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` via `HumansCampControllerBase` |
| `ContainerController` | All non-public actions | `_authorizationService.AuthorizeAsync(User, target, ContainerOperationRequirement.{Manage, Place})` (resource-based) |
| `EventsController` | Barrio-event submit/create/edit/update/withdraw | `_authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.SubmitEvent)` via `HumansCampControllerBase.ResolveCampEventManagementAsync` (resource-based); plus owner-or-`RoleChecks.IsEventsAdmin` gate on Edit/Update endpoints |
| `ExpensesController` | Report submit/edit/withdraw/line CRUD | `_authService.AuthorizeAsync(User, report, ExpenseReportOperationRequirement.*)` (resource-based) |
| `StoreController` | Order CRUD/pay | `_authService.AuthorizeAsync(User, order, StoreOrderOperationRequirement.*)` (resource-based) |
| `IssuesController` | All mutating actions | `_authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)` (resource-based) |
| `CityPlanningController` / `CityPlanningApiController` | All actions except `Index`/`GetState` | `RoleChecks.IsCampAdmin(User)` and lead-of-camp checks; three API endpoints also call `_authorizationService.AuthorizeAsync` |
| `FeedbackController` | `Index`, `Detail`, `PostMessage` | `RoleChecks.IsFeedbackAdmin(User)` to determine admin vs user view |
| `ProfileController.AddRole/EndRole` | After `[Authorize(Policy)]` attribute | `_authorizationService.AuthorizeAsync(User, roleName, RoleAssignmentOperationRequirement.Manage)` enforces the role-list filter |
| `ProfileController` email-edit endpoints (~19 actions) | After class-level `[Authorize]` | `_authorizationService.AuthorizeAsync(User, userId, UserEmailOperations.Edit)` (resource-based) |
| `TicketController.Index` | After class-level policy | `RoleChecks.CanAccessFinance(User)` toggles finance-only metrics |
| `MembershipRequiredFilter` | All requests | `RoleChecks.BypassesMembershipRequirement(user)` skips active-member check for privileged roles |
| `HangfireAuthorizationFilter` | Hangfire dashboard | `RoleChecks.IsAdmin(User)` |
| `AgentController.Ask` | Per-request | `_auth.AuthorizeAsync(User, user.Id, PolicyNames.AgentRateLimit)` (resource-based) |

---

## 5. Canonical Policy Name Table

These are the named ASP.NET policies registered in `AuthorizationPolicyExtensions.AddHumansAuthorizationPolicies`. Each maps from the current authorization dialect(s) to a single canonical name. **Phase 1 complete:** every policy in this table is now registered.

| Canonical Policy Name | Roles | Current Sources |
|---|---|---|
| `AdminOnly` | Admin | `PolicyNames.AdminOnly`, `RoleChecks.IsAdmin` |
| `AnyAdminRole` | Admin, Board, HumanAdmin, TeamsAdmin, CampAdmin, TicketAdmin, EventsAdmin, FeedbackAdmin, FinanceAdmin, StoreAdmin, CantinaAdmin, NoInfoAdmin, VolunteerCoordinator, ConsentCoordinator | `PolicyNames.AnyAdminRole` (admin-shell entry-point gate) |
| `BoardOnly` | Board | `PolicyNames.BoardOnly` |
| `BoardOrAdmin` | Board, Admin | `PolicyNames.BoardOrAdmin`, `RoleChecks.IsAdminOrBoard` |
| `HumanAdminBoardOrAdmin` | HumanAdmin, Board, Admin | `PolicyNames.HumanAdminBoardOrAdmin`, `RoleChecks.IsHumanAdminBoardOrAdmin` |
| `HumanAdminOrAdmin` | HumanAdmin, Admin | `PolicyNames.HumanAdminOrAdmin` |
| `TeamsAdminBoardOrAdmin` | TeamsAdmin, Board, Admin | `PolicyNames.TeamsAdminBoardOrAdmin`, `RoleChecks.IsTeamsAdminBoardOrAdmin` |
| `CampAdminOrAdmin` | CampAdmin, Admin | `PolicyNames.CampAdminOrAdmin`, `RoleChecks.IsCampAdmin` |
| `TicketAdminBoardOrAdmin` | TicketAdmin, Admin, Board | `PolicyNames.TicketAdminBoardOrAdmin`, `RoleChecks.CanAccessTickets` |
| `TicketAdminOrAdmin` | TicketAdmin, Admin | `PolicyNames.TicketAdminOrAdmin`, `RoleChecks.CanManageTickets` |
| `FeedbackAdminOrAdmin` | FeedbackAdmin, Admin | `PolicyNames.FeedbackAdminOrAdmin`, `RoleChecks.IsFeedbackAdmin` |
| `FinanceAdminOrAdmin` | FinanceAdmin, Admin | `PolicyNames.FinanceAdminOrAdmin`, `RoleChecks.IsFinanceAdmin`, `RoleChecks.CanAccessFinance` |
| `EventsAdminOrAdmin` | EventsAdmin, Admin | `PolicyNames.EventsAdminOrAdmin` |
| `CantinaAdminOrAdmin` | CantinaAdmin, Admin | `PolicyNames.CantinaAdminOrAdmin` (Cantina coordinator surface) |
| `StoreCatalogAdmin` | StoreAdmin, FinanceAdmin, Admin | `PolicyNames.StoreCatalogAdmin`, `RoleChecks.CanAdministerStore` |
| `ReviewQueueAccess` | ConsentCoordinator, VolunteerCoordinator, Board, Admin | `PolicyNames.ReviewQueueAccess`, `RoleChecks.CanAccessReviewQueue` |
| `ConsentCoordinatorBoardOrAdmin` | ConsentCoordinator, Board, Admin | `PolicyNames.ConsentCoordinatorBoardOrAdmin` |
| `ShiftDashboardAccess` | Admin, NoInfoAdmin, VolunteerCoordinator | `PolicyNames.ShiftDashboardAccess`, `ShiftRoleChecks.CanAccessDashboard` |
| `ShiftDepartmentManager` | Admin, NoInfoAdmin, VolunteerCoordinator OR any team manager/coordinator | `PolicyNames.ShiftDepartmentManager` (composite — `IsAnyTeamManagerOrCoordinatorHandler`) |
| `VolunteerTrackingWrite` | Admin, VolunteerCoordinator | `PolicyNames.VolunteerTrackingWrite` |
| `PrivilegedSignupApprover` | Admin, NoInfoAdmin | `PolicyNames.PrivilegedSignupApprover`, `ShiftRoleChecks.IsPrivilegedSignupApprover` |
| `VolunteerManager` | Admin, VolunteerCoordinator | `PolicyNames.VolunteerManager`, `RoleChecks.IsVolunteerManager` |
| `ActiveMemberOrShiftAccess` | ActiveMember claim OR ShiftDashboardAccess OR TeamsAdmin/Board/Admin | `PolicyNames.ActiveMemberOrShiftAccess` (composite — `ActiveMemberOrShiftAccessHandler`) |
| `IsActiveMember` | ActiveMember claim OR TeamsAdmin/Board/Admin | `PolicyNames.IsActiveMember` (composite — `IsActiveMemberHandler`) |
| `HumanAdminOnly` | HumanAdmin AND NOT (Admin OR Board) | `PolicyNames.HumanAdminOnly` (composite — `HumanAdminOnlyHandler`) |
| `MedicalDataViewer` | Admin, NoInfoAdmin | `PolicyNames.MedicalDataViewer`, `ShiftRoleChecks.CanViewMedical` |
| `AgentRateLimit` | (per-user rate-limit) | `PolicyNames.AgentRateLimit` (resource-based — `AgentRateLimitHandler`) |

### Notes on Policy Design

- `ShiftDashboardAccess` and `ShiftDepartmentManager` are intentionally distinct: dashboard access is role-list-based, department manager additionally permits any team manager/coordinator (composite via `IsAnyTeamManagerOrCoordinatorHandler`).
- `ActiveMemberOrShiftAccess` and `IsActiveMember` are composite policies that check the `ActiveMember` claim OR fall back to role-based access. They use custom `IAuthorizationRequirement` + handler rather than a simple `RequireRole`.
- `HumanAdminOnly` is a composite policy used for the nav "Humans" link that only shows when the user has HumanAdmin but not the broader Board/Admin access.
- `MedicalDataViewer` is a data-access policy, not a page-access policy. It controls whether medical fields are visible within pages the user already has access to.
- `AnyAdminRole` gates the admin-shell entry point (`/Admin`). Sidebar items inside the shell are filtered per-item by `AdminSidebarViewComponent` against each item's policy. The role list mirrors the top-nav check in `_Layout.cshtml` and includes the grantable `CantinaAdmin` role added with the Cantina coordinator surface (feature #36).
- Object-relative policies (coordinator of specific team, camp lead of specific camp, camp-event submitter, budget category for coordinator's department, manageable role for HumanAdmin, expense reports, store orders, containers, issues, user-email edits, agent rate-limit) are implemented as resource-based authorization handlers — see §6.

---

## 6. Resource-Based Authorization Handlers

Resource-based authorization handlers are subclasses of `AuthorizationHandler<TRequirement, TResource>` (or `AuthorizationHandler<TRequirement>` / `IAuthorizationHandler` directly when the same handler covers multiple resource shapes) that evaluate whether a user can perform an operation on a specific resource instance. They are invoked via `IAuthorizationService.AuthorizeAsync(User, resource, requirement)` from controllers (or controller base classes).

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `TeamAuthorizationHandler` | `TeamOperationRequirement` (`ManageCoordinators`) | `TeamInfo` | `src/Humans.Web/Authorization/Requirements/TeamAuthorizationHandler.cs` |
| `CampAuthorizationHandler` | `CampOperationRequirement` (`Manage`, `SubmitEvent`) | `CampLookup` / `Camp` entity / camp id (`Guid`) | `src/Humans.Web/Authorization/Requirements/CampAuthorizationHandler.cs` |
| `BudgetAuthorizationHandler` | `BudgetOperationRequirement` (`Edit`) | `BudgetCategorySnapshot` | `src/Humans.Web/Authorization/Requirements/BudgetAuthorizationHandler.cs` |
| `ContainerAuthorizationHandler` | `ContainerOperationRequirement` (`Manage`, `Place`) | `ContainerAuthorizationTarget` | `src/Humans.Web/Authorization/Requirements/ContainerAuthorizationHandler.cs` |
| `StoreOrderAuthorizationHandler` | `StoreOrderOperationRequirement` (`View`, `Create`, `AddLine`, `RemoveLine`, `EditCounterparty`, `Pay`) | `OrderDto` / `StoreOrderCreateContext` | `src/Humans.Web/Authorization/Requirements/StoreOrderAuthorizationHandler.cs` |
| `ExpenseReportAuthorizationHandler` | `ExpenseReportOperationRequirement` | `ExpenseReportDto` | `src/Humans.Web/Authorization/Requirements/ExpenseReportAuthorizationHandler.cs` |
| `IbanAccessHandler` | `IbanAccessRequirement` | (intrinsic — fields on requirement) | `src/Humans.Web/Authorization/Requirements/IbanAccessHandler.cs` |
| `IssuesAuthorizationHandler` | `IssuesOperationRequirement` (`Handle`) | `IssueDetail` | `src/Humans.Web/Authorization/Requirements/IssuesAuthorizationHandler.cs` |
| `UserEmailAuthorizationHandler` | `UserEmailOperationRequirement` (`Edit`) | `Guid` (target user id) | `src/Humans.Web/Authorization/Requirements/UserEmailAuthorizationHandler.cs` |
| `RoleAssignmentAuthorizationHandler` | `RoleAssignmentOperationRequirement` (`Manage`) | `string` (roleName) | `src/Humans.Application/Authorization/RoleAssignmentAuthorizationHandler.cs` |
| `AgentRateLimitHandler` | `AgentRateLimitRequirement` | `Guid` (user id) | `src/Humans.Web/Authorization/Handlers/AgentRateLimitHandler.cs` |

Composite (non-resource) handlers registered alongside the above:

| Handler | Requirement | Path |
|---|---|---|
| `ActiveMemberOrShiftAccessHandler` | `ActiveMemberOrShiftAccessRequirement` | `src/Humans.Web/Authorization/Requirements/ActiveMemberOrShiftAccessHandler.cs` |
| `IsActiveMemberHandler` | `IsActiveMemberRequirement` | `src/Humans.Web/Authorization/Requirements/IsActiveMemberHandler.cs` |
| `HumanAdminOnlyHandler` | `HumanAdminOnlyRequirement` | `src/Humans.Web/Authorization/Requirements/HumanAdminOnlyHandler.cs` |
| `IsAnyTeamManagerOrCoordinatorHandler` | `IsAnyTeamManagerOrCoordinatorRequirement` | `src/Humans.Web/Authorization/Requirements/IsAnyTeamManagerOrCoordinatorHandler.cs` |

### `IAuthorizationService.AuthorizeAsync` Call Sites

| File | Line | Call |
|---|---|---|
| `src/Humans.Web/Controllers/HumansTeamControllerBase.cs` | 23 | `AuthorizeAsync(User, team, TeamOperationRequirement.ManageCoordinators)` |
| `src/Humans.Web/Controllers/HumansCampControllerBase.cs` | 24 | `AuthorizeAsync(User, campId, CampOperationRequirement.Manage)` |
| `src/Humans.Web/Controllers/HumansCampControllerBase.cs` | 55 | `AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` |
| `src/Humans.Web/Controllers/HumansCampControllerBase.cs` | 85 | `AuthorizeAsync(User, camp, CampOperationRequirement.SubmitEvent)` |
| `src/Humans.Web/Controllers/BudgetController.cs` | 33 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` |
| `src/Humans.Web/Controllers/BudgetController.cs` | 96 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` |
| `src/Humans.Web/Controllers/BudgetController.cs` | 116 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` |
| `src/Humans.Web/Controllers/BudgetController.cs` | 122 | `AuthorizeAsync(User, detail.Category, BudgetOperationRequirement.Edit)` |
| `src/Humans.Web/Controllers/BudgetController.cs` | 226 | `AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)` |
| `src/Humans.Web/Controllers/ContainerController.cs` | 24 | `AuthorizeAsync(User, target, requirement)` (private helper) |
| `src/Humans.Web/Controllers/ExpensesController.cs` | 134, 457, 503, 526, 574, 598, 685 | `AuthorizeAsync(User, report, ExpenseReportOperationRequirement.X)` |
| `src/Humans.Web/Controllers/StoreController.cs` | 55, 58, 59, 60, 75, 109, 127, 156, 182, 204, 229 | `AuthorizeAsync(User, order, StoreOrderOperationRequirement.X)` (and `StoreOrderCreateContext` for Create) |
| `src/Humans.Web/Controllers/IssuesController.cs` | 195, 265, 311, 338, 365, 390 | `AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)` |
| `src/Humans.Web/Controllers/CityPlanningApiController.cs` | 276, 301, 339 | `AuthorizeAsync(User, ...)` (resource-based) |
| `src/Humans.Web/Controllers/ProfileController.cs` | 677, 710, 755, 797, 834, 871, 908, 928, 972, 1047, 1063, 1089, 1122, 1148, 1174, 1350, 1376, 1420 | `AuthorizeAsync(User, userId, UserEmailOperations.Edit)` |
| `src/Humans.Web/Controllers/ProfileController.cs` | 1826 | `AuthorizeAsync(User, PolicyNames.TicketAdminBoardOrAdmin)` (onsite-chip visibility gate) |
| `src/Humans.Web/Controllers/ProfileController.cs` | 2341 | `AuthorizeAsync(User, model.RoleName, RoleAssignmentOperationRequirement.Manage)` (AddRole) |
| `src/Humans.Web/Controllers/ProfileController.cs` | 2394 | `AuthorizeAsync(User, roleAssignment.RoleName, RoleAssignmentOperationRequirement.Manage)` (EndRole) |
| `src/Humans.Web/Controllers/AgentController.cs` | 48 | `AuthorizeAsync(User, user.Id, PolicyNames.AgentRateLimit)` |
| `src/Humans.Web/TagHelpers/AuthorizeViewTagHelper.cs` | 54 | `AuthorizeAsync(user, Policy)` (driver of `<authorize-policy>` view tags) |
| `src/Humans.Web/ViewComponents/AdminSidebarViewComponent.cs` | 31 | `AuthorizeAsync(HttpContext.User, null, item.Policy)` (filters admin sidebar) |

---

## 7. Notes / Known Deviations

- **`DevSeedController.ResetDashboard` uses `[Authorize(Roles = RoleNames.Admin)]`** rather than `[Authorize(Policy = PolicyNames.AdminOnly)]`. Single-purpose dev/test endpoint; behaviourally identical to AdminOnly.
- The Events Guide controllers and `_Layout.cshtml` Events sub-dropdowns have all been migrated to `PolicyNames.EventsAdminOrAdmin` (Phase-1 cleanup complete — verified 2026-05-28).

---

## Appendix: Role Reference

### RoleNames Constants

| Constant | Value |
|---|---|
| `Admin` | `"Admin"` |
| `Board` | `"Board"` |
| `ConsentCoordinator` | `"ConsentCoordinator"` |
| `VolunteerCoordinator` | `"VolunteerCoordinator"` |
| `TeamsAdmin` | `"TeamsAdmin"` |
| `CampAdmin` | `"CampAdmin"` |
| `TicketAdmin` | `"TicketAdmin"` |
| `NoInfoAdmin` | `"NoInfoAdmin"` |
| `EventsAdmin` | `"EventsAdmin"` |
| `FeedbackAdmin` | `"FeedbackAdmin"` |
| `HumanAdmin` | `"HumanAdmin"` |
| `FinanceAdmin` | `"FinanceAdmin"` |
| `StoreAdmin` | `"StoreAdmin"` |
| `CantinaAdmin` | `"CantinaAdmin"` |

### RoleChecks Methods → Canonical Policy Mapping

| Method | Canonical Policy |
|---|---|
| `IsAdmin` | `AdminOnly` |
| `IsBoard` | (no standalone policy — used in `GetAssignableRoles` / `CanManageRole`) |
| `IsAdminOrBoard` | `BoardOrAdmin` |
| `IsTeamsAdmin` | (no standalone policy — used in TeamAdminController toggle-management check) |
| `IsTeamsAdminBoardOrAdmin` | `TeamsAdminBoardOrAdmin` |
| `IsCampAdmin` | `CampAdminOrAdmin` |
| `CanAccessReviewQueue` | `ReviewQueueAccess` |
| `CanAccessTickets` | `TicketAdminBoardOrAdmin` |
| `CanManageTickets` | `TicketAdminOrAdmin` |
| `IsHumanAdminBoardOrAdmin` | `HumanAdminBoardOrAdmin` |
| `IsHumanAdmin` | `HumanAdminOnly` (composite, when negated against Board/Admin) |
| `IsFeedbackAdmin` | `FeedbackAdminOrAdmin` |
| `IsFinanceAdmin` / `CanAccessFinance` | `FinanceAdminOrAdmin` |
| `CanAdministerStore` | `StoreCatalogAdmin` |
| `IsVolunteerManager` | `VolunteerManager` |
| `BypassesMembershipRequirement` | (filter-level in `MembershipRequiredFilter`, not a page policy) |
| `GetAssignableRoles` / `CanManageRole` | `RoleAssignmentOperationRequirement.Manage` (resource-based, see §6) |

### ShiftRoleChecks Methods → Canonical Policy Mapping

| Method | Canonical Policy |
|---|---|
| `IsPrivilegedSignupApprover` | `PrivilegedSignupApprover` |
| `CanManageDepartment` | `ShiftDepartmentManager` (role-list portion; composite extends with team-manager OR) |
| `CanAccessDashboard` | `ShiftDashboardAccess` |
| `CanViewMedical` | `MedicalDataViewer` |
