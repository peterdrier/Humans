# Authorization Inventory

Phase 0 of the [first-class authorization transition plan](plans/2026-04-03-first-class-authorization-transition.md).

Generated 2026-04-03. Refreshed 2026-04-25. Covers every `[Authorize(Policy)]` / `[Authorize(Roles)]` attribute on controllers and actions in `src/Humans.Web/Controllers/`, every `RoleChecks.*` / `ShiftRoleChecks.*` invocation across `src/Humans.Web/` and `src/Humans.Application/`, every `IAuthorizationService.AuthorizeAsync` call site, and every `AuthorizationHandler<T, R>` under `src/Humans.Web/Authorization/` and `src/Humans.Application/Authorization/`.

The codebase has migrated from `[Authorize(Roles = RoleGroups.X)]` to `[Authorize(Policy = PolicyNames.X)]`. The `Source` column reflects the constant referenced in the attribute as it appears in the code today.

---

## 1. Controller Authorization Map

### Admin Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `AdminController` | Class | `[Route("Admin")]` only — no class-level `[Authorize]` | — |
| `AdminController.Index` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `AdminController.PurgeHuman` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `AdminController.Logs` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `AdminController.Configuration` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `AdminController.DbVersion` | Action | `AllowAnonymous` | Override |
| `AdminController.DbStats` / `ResetDbStats` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `AdminController.ClearHangfireLocks` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `AdminController.CacheStats` / `ResetCacheStats` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `AdminController.AudienceSegmentation` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `AdminDuplicateAccountsController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `AdminMergeController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `AdminLegalDocumentsController` | Class | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `EmailController` | Class | `Admin` | `PolicyNames.AdminOnly` |

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
| `GoogleController.CheckEmailMismatches` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.EmailBackfillReview` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.ApplyEmailBackfill` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.Sync` | Action | `TeamsAdmin, Board, Admin` | `PolicyNames.TeamsAdminBoardOrAdmin` |
| `GoogleController.SyncPreview` | Action | `TeamsAdmin, Board, Admin` | `PolicyNames.TeamsAdminBoardOrAdmin` |
| `GoogleController.SyncExecute` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.SyncExecuteAll` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.CheckDriveActivity` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `GoogleController.GoogleSyncResourceAudit` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `GoogleController.HumanGoogleSyncAudit` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `GoogleController.ProvisionEmail` | Action | `HumanAdmin, Admin` | `PolicyNames.HumanAdminOrAdmin` |
| `GoogleController.Accounts` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.ProvisionAccount` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.SuspendAccount` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.ReactivateAccount` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.ResetPassword` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.LinkAccount` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.SyncOutbox` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.CheckEmailRenames` / `EmailRenames` / `FixEmailRename` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GoogleController.Index` | Action | `Admin` | `PolicyNames.AdminOnly` |

### Tickets Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `TicketController` | Class | `TicketAdmin, Admin, Board` | `PolicyNames.TicketAdminBoardOrAdmin` |
| `TicketController.Sync` | Action | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `TicketController.FullResync` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `TicketController.ParticipationBackfill` (GET/POST) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `TicketController.ExportAttendees` | Action | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |
| `TicketController.ExportOrders` | Action | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` |

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
| `CampaignController.Activate/Complete` | Action | `Admin` | `PolicyNames.AdminOnly` |
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

### Board Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `BoardController` | Class | `Board, Admin` | `PolicyNames.BoardOrAdmin` |

### Onboarding Review Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `OnboardingReviewController` | Class | `ConsentCoordinator, VolunteerCoordinator, Board, Admin` | `PolicyNames.ReviewQueueAccess` |
| `OnboardingReviewController.Clear` | Action | `ConsentCoordinator, Board, Admin` | `PolicyNames.ConsentCoordinatorBoardOrAdmin` |
| `OnboardingReviewController.Flag` | Action | `ConsentCoordinator, Board, Admin` | `PolicyNames.ConsentCoordinatorBoardOrAdmin` |
| `OnboardingReviewController.Reject` | Action | `ConsentCoordinator, Board, Admin` | `PolicyNames.ConsentCoordinatorBoardOrAdmin` |
| `OnboardingReviewController.BoardVoting` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `OnboardingReviewController.BoardVotingDetail` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `OnboardingReviewController.Vote` | Action | `Board` | `PolicyNames.BoardOnly` |
| `OnboardingReviewController.Finalize` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |

### Governance / Application Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `GovernanceController` | Class | `[Authorize]` (authenticated) | — |
| `GovernanceController.Roles` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `ApplicationController` | Class | `[Authorize]` (authenticated) | — |
| `ApplicationController.Applications` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `ApplicationController.ApplicationDetail` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |

### Profile / Contacts Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ProfileController` | Class | `[Authorize]` (authenticated) | — |
| `ProfileController.VerifyEmail` | Action | `AllowAnonymous` | Override |
| `ProfileController.AdminList` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.AdminDetail` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.AdminOutbox` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.SuspendHuman` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.UnsuspendHuman` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.ApproveVolunteer` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.RejectSignup` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.AddRole` (GET/POST) | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.EndRole` | Action | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |
| `ProfileController.AddRole/EndRole` runtime guards | In-method | `_authorizationService.AuthorizeAsync(User, roleName, RoleAssignmentOperationRequirement.Manage)` | Resource-based (see §6) |
| `ContactsController` | Class | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` |

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
| `CampController.Index/Details/SeasonDetails` | Action | `AllowAnonymous` | Override |
| `CampController.*` (Edit/Register/Join/Leave/etc.) | Action | `[Authorize]` (authenticated) | — |
| `CampController` runtime guards | In-method | `_authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` via `HumansCampControllerBase` | Resource-based (see §6) |
| `CampAdminController` | Class | `CampAdmin, Admin` | `PolicyNames.CampAdminOrAdmin` |
| `CampAdminController.Delete` | Action | `Admin` | `PolicyNames.AdminOnly` |

### Shifts Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ShiftsController` | Class | `[Authorize]` (authenticated) | — |
| `ShiftsController.Settings` (GET/POST) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `ShiftAdminController` | Class | `[Authorize]` (authenticated) | Coordinator checks at runtime via `HumansTeamControllerBase` |
| `ShiftDashboardController` | Class | `Admin, NoInfoAdmin, VolunteerCoordinator` | `PolicyNames.ShiftDashboardAccess` |

### Volunteer Management Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `VolController` | Class | `[Authorize]` (authenticated) | — |
| `VolController.Settings` (GET/POST) | Action | `Admin` | `PolicyNames.AdminOnly` |
| `VolController` runtime guards (Management/Dashboard/etc.) | In-method | `ShiftRoleChecks.CanAccessDashboard(User)` / `RoleChecks.IsVolunteerManager(User)` | RoleChecks helpers |

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
| `CityPlanningApiController` runtime guards | In-method | `RoleChecks.IsCampAdmin(User)` and lead-of-camp checks | RoleChecks helper |

### Feedback Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `FeedbackController` | Class | `[Authorize]` (authenticated) | — |
| `FeedbackController.UpdateStatus` | Action | `FeedbackAdmin, Admin` | `PolicyNames.FeedbackAdminOrAdmin` |
| `FeedbackController.UpdateAssignment` | Action | `FeedbackAdmin, Admin` | `PolicyNames.FeedbackAdminOrAdmin` |
| `FeedbackController.SetGitHubIssue` | Action | `FeedbackAdmin, Admin` | `PolicyNames.FeedbackAdminOrAdmin` |
| `FeedbackController` runtime guards | In-method | `RoleChecks.IsFeedbackAdmin(User)` to drive admin-vs-user view | RoleChecks helper |
| `FeedbackApiController` | Class | (no class-level `[Authorize]`) | — |

### Guide Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `GuideController` | Class | (no class-level `[Authorize]`) | — |
| `GuideController.Index` | Action | `AllowAnonymous` | Override |
| `GuideController.Document` | Action | `AllowAnonymous` | Override |
| `GuideController.Refresh` | Action | `Admin` | `PolicyNames.AdminOnly` |

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

### Dev Seed (test data)

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `DevSeedController` | Class | `[Authorize]` (authenticated) | — |
| `DevSeedController.SeedBudget` | Action | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` |
| `DevSeedController.SeedDashboard` | Action | `Admin, NoInfoAdmin, VolunteerCoordinator` | `PolicyNames.ShiftDashboardAccess` |

### Guest / Consent / Notifications

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `GuestController` | Class | `[Authorize]` (authenticated) | — |
| `GuestController.CommunicationPreferences` (GET/POST) | Action | `AllowAnonymous` (token-validated) | Override (see WARNING in source) |
| `ConsentController` | Class | `[Authorize]` (authenticated) | — |
| `NotificationController` | Class | `[Authorize]` (authenticated) | — |

### Public / API

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ProfileApiController` | Class | `[Authorize]` (authenticated) | — |
| `LegalController` | Class | `AllowAnonymous` | — |
| `CampApiController` | Class | `AllowAnonymous` | — |
| `ColorPaletteController` | Class | `AllowAnonymous` | — |
| `LogApiController` | Class | (no class-level `[Authorize]`) | — |
| `TimezoneApiController` | Class | (no class-level `[Authorize]`) | — |
| `HangfireAuthorizationFilter` | Filter | `RoleChecks.IsAdmin(User)` | Admin only |

---

## 2. View Authorization Map

### Nav Layout (`_Layout.cshtml`)

| Line | Check | Controls |
|---|---|---|
| 56 | `ActiveMember` claim OR `RoleChecks.IsTeamsAdminBoardOrAdmin` | Shifts nav link visibility |
| 57 | Above OR `ShiftRoleChecks.CanAccessDashboard` | Shifts nav link visibility |
| 63 | `RoleChecks.CanAccessVolunteers` | "V" (Vol Management) nav link |
| 70 | `RoleChecks.CanAccessReviewQueue` | Review nav link |
| 76 | `RoleChecks.IsAdminOrBoard` | Voting nav link |
| 82 | `RoleChecks.IsAdminOrBoard` | Board nav link |
| 88 | `RoleChecks.IsHumanAdmin` AND NOT `IsAdminOrBoard` | "Humans" nav link (standalone HumanAdmin) |
| 94 | `RoleChecks.IsAdmin` | Admin nav link |
| 94 | `RoleChecks.IsAdmin` | Google nav link |
| 103 | `RoleChecks.CanAccessTickets` | Tickets nav link |
| 109 | `RoleChecks.IsFeedbackAdmin` | Feedback nav link |
| 121 | `RoleChecks.CanAccessFinance` | Finance nav link |

### Login Partial (`_LoginPartial.cshtml`)

| Line | Check | Controls |
|---|---|---|
| 13 | `RoleChecks.IsTeamsAdminBoardOrAdmin` | `isActiveMember` flag for Governance dropdown link |

### Shift Views

| View | Check | Controls |
|---|---|---|
| `Shifts/Index.cshtml:54` | `ShiftRoleChecks.CanManageDepartment` | "Manage" button |
| `Shifts/Index.cshtml:58` | `RoleChecks.IsAdmin` | "Event Settings" link |
| `Shifts/NoActiveEvent.cshtml:8` | `RoleChecks.IsAdmin` | "Create Event" link |
| `ShiftAdmin/Index.cshtml` | `Model.CanManageShifts` | Create/edit shift buttons |
| `ShiftAdmin/Index.cshtml` | `Model.CanApproveSignups` | Approve/reject signup buttons |
| `ShiftAdmin/Index.cshtml` | `Model.CanViewMedical` | Medical badge visibility |

### Volunteer Management Views

| View | Check | Controls |
|---|---|---|
| `Vol/_VolLayout.cshtml:26` | `ShiftRoleChecks.CanAccessDashboard` | Dashboard nav tab |
| `Vol/_VolLayout.cshtml:39` | `RoleChecks.IsAdmin` | Settings nav tab |
| `Vol/Management.cshtml:37` | `RoleChecks.IsAdmin` | "Create Department" button |
| `Vol/Management.cshtml:106` | `RoleChecks.IsAdmin` | "Event Settings" button |
| `Vol/NoActiveEvent.cshtml:9` | `RoleChecks.IsAdmin` | "Create Event" link |
| `Vol/DepartmentDetail.cshtml:52` | `Model.IsCoordinator` | Pending request count badge |
| `Vol/ChildTeamDetail.cshtml:96` | `Model.IsCoordinator` | Pending requests section |
| `Vol/Settings.cshtml:79` | `Model.IsShiftBrowsingOpen` | Shift browsing toggle |
| `Vol/Shifts.cshtml:100` | `Model.ShowSignups` | Signup column visibility |

### Profile Views

| View | Check | Controls |
|---|---|---|
| `Profile/Index.cshtml:12` | `RoleChecks.IsHumanAdminBoardOrAdmin` | "Admin Detail" link |
| `Profile/Index.cshtml:63` | `RoleChecks.IsTeamsAdminBoardOrAdmin` | Team view mode for non-own profiles |
| `Profile/AdminDetail.cshtml` | `Model.IsSuspended/IsApproved/IsRejected` | Status badges and action buttons |
| `Profile/AdminDetail.cshtml:309` | `Model.HasProfile && !IsApproved && !IsSuspended` | "Approve" button |
| `Profile/AdminDetail.cshtml:317` | `Model.IsSuspended` | "Unsuspend" button |
| `Profile/AdminDetail.cshtml:336` | `Model.HasProfile && !IsRejected` | "Suspend" button |

### Board Views

| View | Check | Controls |
|---|---|---|
| `OnboardingReview/BoardVotingDetail.cshtml:115` | `RoleChecks.IsBoard` | Vote casting form |
| `OnboardingReview/BoardVotingDetail.cshtml:156` | `Model.CanFinalize` | Finalize button |
| `OnboardingReview/Detail.cshtml:30` | `Model.HasPendingApplication` | Application tab |

### Team Views

| View | Check | Controls |
|---|---|---|
| `Team/Index.cshtml:23` | `Model.CanCreateTeam` | "Create Team" button |
| `Team/Summary.cshtml:8,31,78,130` | `RoleChecks.IsAdminOrBoard` | Edit/delete/archive buttons |
| `Team/EditTeam.cshtml:72` | `RoleChecks.IsAdmin` | "Hidden" checkbox |
| `Team/_TeamCard.cshtml:39` | `Model.IsCurrentUserCoordinator` | "Manage" link |

### Camp Views

| View | Check | Controls |
|---|---|---|
| `Camp/Index.cshtml:11` | `RoleChecks.IsCampAdmin` | "Camp Admin" link |
| `Camp/Details.cshtml:184` | `Model.IsCurrentUserLead \| IsCurrentUserCampAdmin` | Edit button |
| `Camp/Details.cshtml:296` | `Model.IsCurrentUserLead \| IsCurrentUserCampAdmin` | Season management |
| `CampAdmin/Index.cshtml:267` | `RoleChecks.IsAdmin` | "Delete Camp" button |

### Ticket Views

| View | Check | Controls |
|---|---|---|
| `Ticket/Index.cshtml:299` | `RoleChecks.CanManageTickets` | "Sync" button |
| `Ticket/Index.cshtml:307` | `RoleChecks.IsAdmin` | "Event Settings" link |

### Campaign Views

| View | Check | Controls |
|---|---|---|
| `Campaign/Detail.cshtml:19` | `RoleChecks.IsAdmin` | Campaign management buttons |
| `Campaign/Detail.cshtml:20` | `RoleChecks.CanManageTickets` | Code generation button |

### Budget Views

| View | Check | Controls |
|---|---|---|
| `Budget/Index.cshtml:6,19` | `Model.IsFinanceAdmin` | Restricted groups, year management |
| `Budget/Summary.cshtml:13` | `Model.IsCoordinator` | Department edit access |
| `Budget/CategoryDetail.cshtml:54+` | `Model.CanEdit` | Line item CRUD |

### Feedback Views

| View | Check | Controls |
|---|---|---|
| `Feedback/Index.cshtml:73,83` | `Model.IsAdmin` | Status dropdown, reply button |
| `Feedback/_Detail.cshtml:9,22` | `Model.IsAdmin` | Admin notes, response form |

### Google Views

| View | Check | Controls |
|---|---|---|
| `Google/Sync.cshtml:41` | `Model.CanExecuteActions` | Action buttons (set from `RoleChecks.IsAdmin`) |
| `Google/Sync.cshtml:204` | `RoleChecks.IsAdminOrBoard` | Audit log visibility |

### Application / Governance Views

| View | Check | Controls |
|---|---|---|
| `Application/Index.cshtml:29` | `Model.IsApprovedColaborador && CanSubmitNew` | Asociado upgrade button |
| `Application/Details.cshtml:54` | `Model.CanWithdraw` | Withdraw button |
| `Application/ApplicationDetail.cshtml:76` | `Model.CanApproveReject` | Approve/Reject buttons |
| `Governance/Index.cshtml:98` | `Model.CanApply` | Apply button |
| `Governance/Index.cshtml:158` | `Model.IsApprovedColaborador && CanApply` | Upgrade apply button |

### Shared Components

| View | Check | Controls |
|---|---|---|
| `ProfileCard/Default.cshtml:139` | `Model.CanSendMessage` | Message button |
| `ProfileCard/Default.cshtml:193` | `Model.CanViewLegalName` | Legal name display |
| `_ShiftsSummaryCard.cshtml:33` | `Model.CanManageShifts` | Manage shifts link |
| `AuthorizeViewTagHelper` | `_authorizationService.AuthorizeAsync(user, Policy)` | Policy-driven `<authorize-policy>` view sections |

---

## 3. Same-Rule-Different-Spelling Table

These entries express the same authorization rule using different syntax across controllers and views.

| Rule | Controller Spelling | View / Helper Spelling |
|---|---|---|
| Admin only | `[Authorize(Policy = PolicyNames.AdminOnly)]` | `RoleChecks.IsAdmin(User)` |
| Board or Admin | `[Authorize(Policy = PolicyNames.BoardOrAdmin)]` | `RoleChecks.IsAdminOrBoard(User)` |
| TeamsAdmin/Board/Admin | `[Authorize(Policy = PolicyNames.TeamsAdminBoardOrAdmin)]` | `RoleChecks.IsTeamsAdminBoardOrAdmin(User)` |
| TicketAdmin/Board/Admin | `[Authorize(Policy = PolicyNames.TicketAdminBoardOrAdmin)]` | `RoleChecks.CanAccessTickets(User)` |
| TicketAdmin or Admin | `[Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]` | `RoleChecks.CanManageTickets(User)` |
| CampAdmin or Admin | `[Authorize(Policy = PolicyNames.CampAdminOrAdmin)]` | `RoleChecks.IsCampAdmin(User)` |
| HumanAdmin/Board/Admin | `[Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]` | `RoleChecks.IsHumanAdminBoardOrAdmin(User)` |
| FeedbackAdmin or Admin | `[Authorize(Policy = PolicyNames.FeedbackAdminOrAdmin)]` | `RoleChecks.IsFeedbackAdmin(User)` |
| FinanceAdmin or Admin | `[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]` | `RoleChecks.IsFinanceAdmin(User)` / `RoleChecks.CanAccessFinance(User)` |
| Review queue access | `[Authorize(Policy = PolicyNames.ReviewQueueAccess)]` | `RoleChecks.CanAccessReviewQueue(User)` |
| Consent coordinator + B/A | `[Authorize(Policy = PolicyNames.ConsentCoordinatorBoardOrAdmin)]` | (no helper) |
| Board only | `[Authorize(Policy = PolicyNames.BoardOnly)]` | `RoleChecks.IsBoard(User)` |
| Shift dashboard access | `[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]` | `ShiftRoleChecks.CanAccessDashboard(User)` |
| Privileged signup approver | `[Authorize(Policy = PolicyNames.PrivilegedSignupApprover)]` (no controller use) | `ShiftRoleChecks.IsPrivilegedSignupApprover(User)` |
| Volunteer manager | `[Authorize(Policy = PolicyNames.VolunteerManager)]` (no controller use) | `RoleChecks.IsVolunteerManager(User)` |
| Volunteer section access | `[Authorize(Policy = PolicyNames.VolunteerSectionAccess)]` (no controller use) | `RoleChecks.CanAccessVolunteers(User)` |
| Resource: team coord/admin | `_authorizationService.AuthorizeAsync(User, team, TeamOperationRequirement.ManageCoordinators)` | `Model.IsCurrentUserCoordinator` (view) |
| Resource: camp lead/admin | `_authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` | `Model.IsCurrentUserLead \| IsCurrentUserCampAdmin` (view) |
| Resource: budget edit | `_authorizationService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)` | `Model.CanEdit` (view) |
| Resource: role assignment | `_authorizationService.AuthorizeAsync(User, roleName, RoleAssignmentOperationRequirement.Manage)` | `RoleChecks.GetAssignableRoles` (UI list) |

---

## 4. Enforcement Gaps

### View-Only (button hidden, no server-side attribute guard)

| Location | Check | Risk |
|---|---|---|
| `Vol/Management.cshtml` — "Create Department" | `RoleChecks.IsAdmin(User)` in view | `VolController.Management` is `[Authorize]` (any authenticated user). The POST create action has runtime checks but no `[Authorize(Policy)]` attribute. |
| `Vol/_VolLayout.cshtml` — Dashboard tab | `ShiftRoleChecks.CanAccessDashboard(User)` in view | Dashboard GET actions guard with `if (!ShiftRoleChecks.CanAccessDashboard(User))` at runtime, not via attribute. |
| `Vol/_VolLayout.cshtml` — Settings tab | `RoleChecks.IsAdmin(User)` in view | Settings action has `[Authorize(Policy = PolicyNames.AdminOnly)]` attribute — **OK, enforced server-side**. |
| `CampAdmin/Index.cshtml` — "Delete Camp" | `RoleChecks.IsAdmin(User)` in view | Delete action has `[Authorize(Policy = PolicyNames.AdminOnly)]` — **OK, narrower than class-level CampAdminOrAdmin**. |
| `Team/Summary.cshtml` — Edit/Delete/Archive links | `RoleChecks.IsAdminOrBoard(User)` in view | Team edit actions have `[Authorize(Policy = PolicyNames.TeamsAdminBoardOrAdmin)]` — view is **stricter** than server (hides from TeamsAdmin). |
| `Ticket/Index.cshtml` — "Event Settings" link | `RoleChecks.IsAdmin(User)` in view | Targets `Shifts/Settings` which has `[Authorize(Policy = PolicyNames.AdminOnly)]` — **OK**. |

### Server-Only (protected endpoint, no visible UI gating)

| Endpoint | Roles | Note |
|---|---|---|
| `GoogleController` actions with broader policies (`Sync`, `SyncPreview`, `CheckDriveActivity`, `GoogleSyncResourceAudit`, `HumanGoogleSyncAudit`, `ProvisionEmail`) | TeamsAdmin/Board/Admin / Board/Admin / HumanAdmin/Board/Admin / HumanAdmin/Admin | Class-level `[Authorize]` was removed; each action has its own policy. The "AND with class-level Admin" effect noted in earlier revisions no longer applies. |
| `ProfileController.AdminOutbox` | `HumanAdminBoardOrAdmin` | No visible button in `AdminList` view (accessed via URL pattern). |

### Runtime-Only Guards (no attribute, enforced in method body)

These actions rely on `if` checks + early return/forbid instead of `[Authorize(Policy)]`:

| Controller | Action | Guard |
|---|---|---|
| `VolController` | `Dashboard`, `Urgent`, `Voluntell`, `SearchVolunteers` | `ShiftRoleChecks.CanAccessDashboard(User)` |
| `VolController` | `DepartmentDetail`, `ChildTeamDetail`, `ApproveJoinRequest`, `RejectJoinRequest` | `RoleChecks.IsVolunteerManager(User)` + coordinator-of-team |
| `VolController` | `Approve`, `Refuse`, `NoShow` | `ShiftRoleChecks.IsPrivilegedSignupApprover(User)` + coordinator-of-team |
| `ShiftAdminController` | All non-public actions | Coordinator-of-team check via `HumansTeamControllerBase.ResolveTeamManagementAsync` (resource-based) |
| `TeamAdminController` | All non-public actions | Coordinator-of-team check via `HumansTeamControllerBase.ResolveTeamManagementAsync` (resource-based) |
| `BudgetController` | `Index`, `Summary`, `CategoryDetail`, line-item CRUD | `_authService.AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` and `_authService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)` |
| `CampController` | All management actions | `_authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` via `HumansCampControllerBase` |
| `CityPlanningController` / `CityPlanningApiController` | All actions except `Index`/`GetState` | `RoleChecks.IsCampAdmin(User)` and lead-of-camp checks |
| `FeedbackController` | `Index`, `Detail` | `RoleChecks.IsFeedbackAdmin(User)` to determine admin vs user view |
| `ProfileController.AddRole/EndRole` | After `[Authorize(Policy)]` attribute | `_authorizationService.AuthorizeAsync(User, roleName, RoleAssignmentOperationRequirement.Manage)` enforces the role-list filter |
| `TicketController.Index` | After class-level policy | `RoleChecks.CanAccessFinance(User)` toggles finance-only metrics |
| `MembershipRequiredFilter` | All requests | `RoleChecks.BypassesMembershipRequirement(user)` skips active-member check for privileged roles |
| `HangfireAuthorizationFilter` | Hangfire dashboard | `RoleChecks.IsAdmin(User)` |

---

## 5. Canonical Policy Name Table

These are the named ASP.NET policies registered in `AuthorizationPolicyExtensions.AddHumansAuthorizationPolicies`. Each maps from the current authorization dialect(s) to a single canonical name. **Phase 1 complete:** every policy in this table is now registered.

| Canonical Policy Name | Roles | Current Sources |
|---|---|---|
| `AdminOnly` | Admin | `PolicyNames.AdminOnly`, `RoleChecks.IsAdmin` |
| `BoardOnly` | Board | `PolicyNames.BoardOnly`, `RoleChecks.IsBoard` |
| `BoardOrAdmin` | Board, Admin | `PolicyNames.BoardOrAdmin`, `RoleChecks.IsAdminOrBoard` |
| `HumanAdminBoardOrAdmin` | HumanAdmin, Board, Admin | `PolicyNames.HumanAdminBoardOrAdmin`, `RoleChecks.IsHumanAdminBoardOrAdmin` |
| `HumanAdminOrAdmin` | HumanAdmin, Admin | `PolicyNames.HumanAdminOrAdmin` |
| `TeamsAdminBoardOrAdmin` | TeamsAdmin, Board, Admin | `PolicyNames.TeamsAdminBoardOrAdmin`, `RoleChecks.IsTeamsAdminBoardOrAdmin` |
| `CampAdminOrAdmin` | CampAdmin, Admin | `PolicyNames.CampAdminOrAdmin`, `RoleChecks.IsCampAdmin` |
| `TicketAdminBoardOrAdmin` | TicketAdmin, Admin, Board | `PolicyNames.TicketAdminBoardOrAdmin`, `RoleChecks.CanAccessTickets` |
| `TicketAdminOrAdmin` | TicketAdmin, Admin | `PolicyNames.TicketAdminOrAdmin`, `RoleChecks.CanManageTickets` |
| `FeedbackAdminOrAdmin` | FeedbackAdmin, Admin | `PolicyNames.FeedbackAdminOrAdmin`, `RoleChecks.IsFeedbackAdmin` |
| `FinanceAdminOrAdmin` | FinanceAdmin, Admin | `PolicyNames.FinanceAdminOrAdmin`, `RoleChecks.IsFinanceAdmin`, `RoleChecks.CanAccessFinance` |
| `ReviewQueueAccess` | ConsentCoordinator, VolunteerCoordinator, Board, Admin | `PolicyNames.ReviewQueueAccess`, `RoleChecks.CanAccessReviewQueue` |
| `ConsentCoordinatorBoardOrAdmin` | ConsentCoordinator, Board, Admin | `PolicyNames.ConsentCoordinatorBoardOrAdmin` |
| `ShiftDashboardAccess` | Admin, NoInfoAdmin, VolunteerCoordinator | `PolicyNames.ShiftDashboardAccess`, `ShiftRoleChecks.CanAccessDashboard` |
| `ShiftDepartmentManager` | Admin, NoInfoAdmin, VolunteerCoordinator | `PolicyNames.ShiftDepartmentManager`, `ShiftRoleChecks.CanManageDepartment` |
| `PrivilegedSignupApprover` | Admin, NoInfoAdmin | `PolicyNames.PrivilegedSignupApprover`, `ShiftRoleChecks.IsPrivilegedSignupApprover` |
| `VolunteerManager` | Admin, VolunteerCoordinator | `PolicyNames.VolunteerManager`, `RoleChecks.IsVolunteerManager` |
| `VolunteerSectionAccess` | TeamsAdmin, Board, Admin, VolunteerCoordinator | `PolicyNames.VolunteerSectionAccess`, `RoleChecks.CanAccessVolunteers` |
| `ActiveMemberOrShiftAccess` | ActiveMember claim OR ShiftDashboardAccess | `PolicyNames.ActiveMemberOrShiftAccess` (composite — `ActiveMemberOrShiftAccessHandler`) |
| `IsActiveMember` | ActiveMember claim OR TeamsAdmin/Board/Admin | `PolicyNames.IsActiveMember` (composite — `IsActiveMemberHandler`) |
| `HumanAdminOnly` | HumanAdmin AND NOT (Admin OR Board) | `PolicyNames.HumanAdminOnly` (composite — `HumanAdminOnlyHandler`) |
| `MedicalDataViewer` | Admin, NoInfoAdmin | `PolicyNames.MedicalDataViewer`, `ShiftRoleChecks.CanViewMedical` |

### Notes on Policy Design

- `ShiftDashboardAccess` and `ShiftDepartmentManager` currently resolve to the same roles but are semantically distinct. Keeping them separate allows future divergence (e.g. per-department manager roles).
- `ActiveMemberOrShiftAccess` and `IsActiveMember` are composite policies that check the `ActiveMember` claim OR fall back to role-based access. They use custom `IAuthorizationRequirement` + handler rather than a simple `RequireRole`.
- `HumanAdminOnly` is a composite policy used for the nav "Humans" link that only shows when the user has HumanAdmin but not the broader Board/Admin access.
- `MedicalDataViewer` is a data-access policy, not a page-access policy. It controls whether medical fields are visible within pages the user already has access to.
- Object-relative policies (coordinator of specific team, camp lead of specific camp, budget category for coordinator's department, manageable role for HumanAdmin) are implemented as resource-based authorization handlers — see §6.

---

## 6. Resource-Based Authorization Handlers

Resource-based authorization handlers are subclasses of `AuthorizationHandler<TRequirement, TResource>` that evaluate whether a user can perform an operation on a specific resource instance. They are invoked via `IAuthorizationService.AuthorizeAsync(User, resource, requirement)` from controllers (or controller base classes).

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `TeamAuthorizationHandler` | `TeamOperationRequirement` (`ManageCoordinators`) | `Team` | `src/Humans.Web/Authorization/Requirements/TeamAuthorizationHandler.cs` |
| `CampAuthorizationHandler` | `CampOperationRequirement` (`Manage`) | `Camp` | `src/Humans.Web/Authorization/Requirements/CampAuthorizationHandler.cs` |
| `BudgetAuthorizationHandler` | `BudgetOperationRequirement` (`Edit`) | `BudgetCategory` | `src/Humans.Web/Authorization/Requirements/BudgetAuthorizationHandler.cs` |
| `RoleAssignmentAuthorizationHandler` | `RoleAssignmentOperationRequirement` (`Manage`) | `string` (roleName) | `src/Humans.Application/Authorization/RoleAssignmentAuthorizationHandler.cs` |

Composite (non-resource) handlers registered alongside the above:

| Handler | Requirement | Path |
|---|---|---|
| `ActiveMemberOrShiftAccessHandler` | `ActiveMemberOrShiftAccessRequirement` | `src/Humans.Web/Authorization/Requirements/ActiveMemberOrShiftAccessHandler.cs` |
| `IsActiveMemberHandler` | `IsActiveMemberRequirement` | `src/Humans.Web/Authorization/Requirements/IsActiveMemberHandler.cs` |
| `HumanAdminOnlyHandler` | `HumanAdminOnlyRequirement` | `src/Humans.Web/Authorization/Requirements/HumanAdminOnlyHandler.cs` |

### `IAuthorizationService.AuthorizeAsync` Call Sites

| File | Line | Call |
|---|---|---|
| `src/Humans.Web/Controllers/ProfileController.cs` | 1537 | `AuthorizeAsync(User, model.RoleName, RoleAssignmentOperationRequirement.Manage)` (AddRole) |
| `src/Humans.Web/Controllers/ProfileController.cs` | 1579 | `AuthorizeAsync(User, roleAssignment.RoleName, RoleAssignmentOperationRequirement.Manage)` (EndRole) |
| `src/Humans.Web/Controllers/HumansTeamControllerBase.cs` | 33 | `AuthorizeAsync(User, team, TeamOperationRequirement.ManageCoordinators)` |
| `src/Humans.Web/Controllers/HumansCampControllerBase.cs` | 32 | `AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` |
| `src/Humans.Web/Controllers/HumansCampControllerBase.cs` | 65 | `AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` |
| `src/Humans.Web/Controllers/BudgetController.cs` | 45 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` |
| `src/Humans.Web/Controllers/BudgetController.cs` | 109 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` |
| `src/Humans.Web/Controllers/BudgetController.cs` | 133 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` |
| `src/Humans.Web/Controllers/BudgetController.cs` | 142 | `AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)` |
| `src/Humans.Web/Controllers/BudgetController.cs` | 254 | `AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)` |
| `src/Humans.Web/TagHelpers/AuthorizeViewTagHelper.cs` | 65 | `AuthorizeAsync(user, Policy)` (driver of `<authorize-policy>` view tags) |

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
| `FeedbackAdmin` | `"FeedbackAdmin"` |
| `HumanAdmin` | `"HumanAdmin"` |
| `FinanceAdmin` | `"FinanceAdmin"` |

### RoleChecks Methods → Canonical Policy Mapping

| Method | Canonical Policy |
|---|---|
| `IsAdmin` | `AdminOnly` |
| `IsBoard` | `BoardOnly` |
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
| `IsVolunteerManager` | `VolunteerManager` |
| `CanAccessVolunteers` | `VolunteerSectionAccess` |
| `BypassesMembershipRequirement` | (filter-level in `MembershipRequiredFilter`, not a page policy) |
| `GetAssignableRoles` / `CanManageRole` | `RoleAssignmentOperationRequirement.Manage` (resource-based, see §6) |

### ShiftRoleChecks Methods → Canonical Policy Mapping

| Method | Canonical Policy |
|---|---|
| `IsPrivilegedSignupApprover` | `PrivilegedSignupApprover` |
| `CanManageDepartment` | `ShiftDepartmentManager` |
| `CanAccessDashboard` | `ShiftDashboardAccess` |
| `CanViewMedical` | `MedicalDataViewer` |
