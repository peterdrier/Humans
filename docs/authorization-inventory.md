# Authorization Inventory

Originally produced as Phase 0 of the [first-class authorization transition plan](plans/2026-04-03-first-class-authorization-transition.md) (kept linked for historical context). **Phase 1 is complete:** every canonical policy in §5 is registered in `AuthorizationPolicyExtensions.AddHumansAuthorizationPolicies`, all controllers use `[Authorize(Policy = PolicyNames.X)]`, the `authorize-policy` TagHelper resolves through `IAuthorizationService`, and views no longer call `RoleChecks.*` / `ShiftRoleChecks.*` directly. **Phase 2 (resource-based authorization)** has shipped its first vertical slices — see §6 (`TeamAuthorizationHandler`, `CampAuthorizationHandler`, `BudgetAuthorizationHandler`, `RoleAssignmentAuthorizationHandler`). **Phase 3 (service-layer enforcement) is cancelled** — see the tombstone in the transition plan.

Generated 2026-04-03. Refreshed 2026-04-25 (Section 2 fully re-scanned 2026-04-26). Covers every `[Authorize(Policy)]` / `[Authorize(Roles)]` attribute on controllers and actions in `src/Humans.Web/Controllers/`, every `RoleChecks.*` / `ShiftRoleChecks.*` invocation across `src/Humans.Web/` and `src/Humans.Application/`, every `IAuthorizationService.AuthorizeAsync` call site, every `authorize-policy` TagHelper attribute and `User.IsInRole` / `Model.X` authorization check across `src/Humans.Web/Views/` and `src/Humans.Web/ViewComponents/`, and every `AuthorizationHandler<T, R>` under `src/Humans.Web/Authorization/` and `src/Humans.Application/Authorization/`.

The `Source` column reflects the constant referenced in the attribute as it appears in the code today.

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

Views express authorization four ways today:

1. **`authorize-policy="PolicyName"` TagHelper attribute** — the dominant pattern. Resolves through `IAuthorizationService.AuthorizeAsync(User, policyName)` via `AuthorizeViewTagHelper`. Hides the element when the policy fails.
2. **`(await AuthService.AuthorizeAsync(User, PolicyNames.X)).Succeeded`** — used when a view needs the boolean for branching, multi-use within the page, or to drive a `var` flag rather than gate one element. Requires `@inject IAuthorizationService AuthService`.
3. **`User.IsInRole(RoleNames.X)` direct calls** — used in two places only (build-hash tooltip in the nav, Refresh button in the Guide layout). Not part of the canonical pattern; kept where the surrounding logic needs a plain `bool` outside the TagHelper rendering pipeline.
4. **`Model.CanX` / `Model.IsX` view-model properties** — for resource-relative checks (coordinator-of-this-team, lead-of-this-camp, can-edit-this-budget) and for status-driven UI (suspended badge, approved badge, etc.). The view does not know about roles; the controller / view-model author resolved authorization upstream.

`RoleChecks.*` and `ShiftRoleChecks.*` are no longer invoked from any view file (Phase 1 retirement complete — verified 2026-04-26).

### Nav Layout (`_Layout.cshtml`)

| Line | Check | Controls |
|---|---|---|
| 33–40 | `User.IsInRole(RoleNames.Admin) \|\| HumanAdmin \|\| TeamsAdmin \|\| CampAdmin \|\| TicketAdmin \|\| FeedbackAdmin \|\| FinanceAdmin \|\| NoInfoAdmin` | Build-hash tooltip on brand link (any admin role sees commit SHA on hover) |
| 101 | `authorize-policy="IsActiveMember"` | City Planning nav link |
| 110 | `authorize-policy="ActiveMemberOrShiftAccess"` | Shifts nav link |
| 113 | `authorize-policy="VolunteerSectionAccess"` | "V" (Vol Management) nav link |
| 116 | `authorize-policy="ReviewQueueAccess"` | Review nav link + queue badges |
| 119 | `authorize-policy="BoardOrAdmin"` | Voting nav link + queue badges |
| 122 | `authorize-policy="BoardOrAdmin"` | Board nav link |
| 125 | `authorize-policy="HumanAdminOnly"` | "Humans" nav link (standalone HumanAdmin without Board/Admin) |
| 128 | `authorize-policy="AdminOnly"` | Admin nav link |
| 131 | `authorize-policy="AdminOnly"` | Google nav link |
| 134 | `authorize-policy="TicketAdminBoardOrAdmin"` | Tickets nav link |
| 137 | `authorize-policy="IsActiveMember"` | Budget nav link |
| 140 | `authorize-policy="FinanceAdminOrAdmin"` | Finance nav link |

### Login Partial (`_LoginPartial.cshtml`)

| Line | Check | Controls |
|---|---|---|
| 44 | `authorize-policy="IsActiveMember"` | Governance link in profile dropdown |

### Guide Layout (`_GuideLayout.cshtml`)

| Line | Check | Controls |
|---|---|---|
| 40 | `User.IsInRole(RoleNames.Admin)` | "Refresh from GitHub" button |

### Shift Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Shifts/Index.cshtml` | 27 | `authorize-policy="ShiftDepartmentManager"` | Dashboard button |
| `Shifts/Index.cshtml` | 28 | `authorize-policy="AdminOnly"` | Settings button |
| `Shifts/NoActiveEvent.cshtml` | 8 | `authorize-policy="AdminOnly"` | "Configure Event Settings" link |
| `ShiftAdmin/Index.cshtml` | 39 | `Model.CanApproveSignups` | Pending approvals card visibility |
| `ShiftAdmin/Index.cshtml` | 66, 558, 594 | `Model.CanViewMedical` | Medical badge in volunteer profile partial |
| `ShiftAdmin/Index.cshtml` | 169, 204, 429, 461, 660, 791, 868, 930 | `Model.CanManageShifts` | Rota/shift edit/delete buttons, add-rota/add-shift forms |
| `ShiftAdmin/Index.cshtml` | 335, 393, 440, 538, 572, 633, 734 | `Model.CanApproveSignups` | Signups column header, approve/reject buttons, signup table cells |

### Volunteer Management Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Vol/_VolLayout.cshtml` | 26 | `authorize-policy="ShiftDashboardAccess"` | Urgent nav tab |
| `Vol/_VolLayout.cshtml` | 31 | `authorize-policy="ShiftDashboardAccess"` | Management nav tab |
| `Vol/_VolLayout.cshtml` | 36 | `authorize-policy="AdminOnly"` | Settings nav tab |
| `Vol/Management.cshtml` | 37 | `authorize-policy="AdminOnly"` | "Change in Settings" button (System Status card) |
| `Vol/Management.cshtml` | 103 | `authorize-policy="AdminOnly"` | Settings action card |
| `Vol/NoActiveEvent.cshtml` | 9 | `authorize-policy="AdminOnly"` | "Configure Event Settings" link |
| `Vol/DepartmentDetail.cshtml` | 52 | `Model.IsCoordinator` | Pending request count badge on child team |
| `Vol/ChildTeamDetail.cshtml` | 92 | `Model.IsCoordinator` (passed to `_RotaCard` partial) | Coordinator-only rota interactions |
| `Vol/ChildTeamDetail.cshtml` | 99 | `Model.IsCoordinator` | Pending join requests card |
| `Vol/Settings.cshtml` | 79 | `Model.IsShiftBrowsingOpen` | Shift-browsing toggle initial state |
| `Vol/Shifts.cshtml` | 98 | `Model.ShowSignups` (passed to `_RotaCard` partial) | Signup column visibility |
| `Vol/_ShiftRow.cshtml` | 58 | `Model.ShowSignups` | Signup status cell |
| `Vol/_RotaCard.cshtml` | 71, 81 | `Model.ShowSignups` | Signup column header + propagation to `_ShiftRow` |

### Profile Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Profile/Index.cshtml` | 15 | `authorize-policy="HumanAdminBoardOrAdmin"` | "Admin" link to AdminDetail |
| `Profile/Index.cshtml` | 63 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminBoardOrAdmin)).Succeeded` | `ProfileCardViewMode.Admin` vs `Public` for non-own profiles |
| `Profile/AdminDetail.cshtml` | 32 | `Model.IsSuspended` | "Suspended" badge |
| `Profile/AdminDetail.cshtml` | 36 | `Model.HasProfile && !Model.IsApproved` | "Pending Approval" badge |
| `Profile/AdminDetail.cshtml` | 40 | `Model.HasProfile && Model.IsApproved` | "Approved" badge |
| `Profile/AdminDetail.cshtml` | 47 | `Model.IsRejected` | Rejection reason banner |
| `Profile/AdminDetail.cshtml` | 371 | `Model.HasProfile && !Model.IsApproved && !Model.IsSuspended` | "Approve Volunteer" button |
| `Profile/AdminDetail.cshtml` | 379 | `Model.IsSuspended` | "Unsuspend" form (vs "Suspend" form in else branch) |
| `Profile/AdminDetail.cshtml` | 398 | `Model.HasProfile && !Model.IsRejected` | "Reject Signup" form |

### Board / Onboarding Review Views

| View | Line | Check | Controls |
|---|---|---|---|
| `OnboardingReview/BoardVotingDetail.cshtml` | 115 | `authorize-policy="BoardOnly"` | Vote casting card |
| `OnboardingReview/BoardVotingDetail.cshtml` | 153 | `Model.CanFinalize` | Finalize decision card |
| `OnboardingReview/Detail.cshtml` | 30 | `Model.HasPendingApplication` | Application tier badge |

### Team Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Team/Index.cshtml` | 23 | `Model.CanCreateTeam` | "Create Team" button |
| `Team/Summary.cshtml` | 18 | `authorize-policy="BoardOrAdmin"` | "Create Team" button |
| `Team/Summary.cshtml` | 38 | `authorize-policy="BoardOrAdmin"` | Actions column header |
| `Team/Summary.cshtml` | 82 | `(await AuthService.AuthorizeAsync(User, PolicyNames.BoardOrAdmin)).Succeeded` | Pending-shift-signup badge link to ShiftAdmin |
| `Team/Summary.cshtml` | 134 | `authorize-policy="BoardOrAdmin"` | Actions column cell (Edit/Deactivate buttons) |
| `Team/EditTeam.cshtml` | 81 | `authorize-policy="AdminOnly"` | "Sensitive team" checkbox |
| `Team/_TeamCard.cshtml` | 39 | `Model.IsCurrentUserCoordinator` | "Manage" link |
| `Team/Details.cshtml` | 36 | `Model.IsCurrentUserCoordinator` | Coordinator role badge |
| `Team/Details.cshtml` | 70, 95 | `Model.CanEditPageContent` | "Edit Page" / "Add Page Content" buttons |

### Camp Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Camp/Index.cshtml` | 11 | `authorize-policy="CampAdminOrAdmin"` | "Camp Admin" link |
| `Camp/Details.cshtml` | 184 | `Model.IsCurrentUserLead \|\| Model.IsCurrentUserCampAdmin` | Placement card visibility |
| `Camp/Details.cshtml` | 348 | `Model.IsCurrentUserLead \|\| Model.IsCurrentUserCampAdmin` | Actions card (Edit) |
| `Camp/Details.cshtml` | 361 | `Model.IsCurrentUserLead && status in (Pending\|Active)` | "Withdraw Season" form |
| `Camp/Details.cshtml` | 370 | `(Model.IsCurrentUserLead \|\| Model.IsCurrentUserCampAdmin) && status == Withdrawn` | "Rejoin Season" form |
| `Camp/Details.cshtml` | 379 | `Model.IsCurrentUserCampAdmin && status == Full` | "Reactivate Season" form |
| `CampAdmin/Index.cshtml` | 345 | `authorize-policy="AdminOnly"` | Danger Zone card (Delete Camp) |

### Ticket Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Ticket/Index.cshtml` | 337 | `authorize-policy="TicketAdminOrAdmin"` | "Sync Now" form |
| `Ticket/Index.cshtml` | 343 | `authorize-policy="AdminOnly"` | "Full Re-sync" form |
| `Ticket/_TicketNav.cshtml` | 26 | `authorize-policy="AdminOnly"` | "Backfill" tab |

### Campaign Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Campaign/Detail.cshtml` | 21 | `var isAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Drives all admin-gated buttons below |
| `Campaign/Detail.cshtml` | 22 | `var canGenerateCodes = (await AuthService.AuthorizeAsync(User, PolicyNames.TicketAdminOrAdmin)).Succeeded` | Drives "Generate Codes" form |
| `Campaign/Detail.cshtml` | 34, 52, 59, 85, 96, 106 | `if (isAdmin)` | Edit, Send Wave, Import Codes, Activate, Complete, Retry-All buttons |
| `Campaign/Detail.cshtml` | 70 | `if (canGenerateCodes && status == Draft)` | "Generate Codes" form |

### Budget Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Budget/Index.cshtml` | 5 | `Model.IsFinanceAdmin` | Filters out ticketing groups for non-finance |
| `Budget/Index.cshtml` | 25 | `Model.IsFinanceAdmin` | "Finance Admin" link |
| `Budget/Index.cshtml` | 115, 116 | `Model.IsFinanceAdmin` | Per-row editability + restricted-group flag |
| `Budget/Summary.cshtml` | 13 | `Model.IsCoordinator` | "Department Detail" link |
| `Budget/CategoryDetail.cshtml` | 51, 125, 162, 178, 249, 259 | `Model.CanEdit` | Editable badge, line-item edit/delete buttons, add-line-item form |

### Feedback Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Feedback/Index.cshtml` | 39 | `Model.IsAdmin && Model.Reporters.Count > 0` | Reporter filter dropdown |
| `Feedback/Index.cshtml` | 56 | `Model.IsAdmin` | Assignee/team/unassigned filter row |
| `Feedback/Index.cshtml` | 145, 155 | `Model.IsAdmin` | Reporter name + assignment badges in list items |
| `Feedback/Index.cshtml` | 183 | `report.NeedsReply && Model.IsAdmin` | "Needs reply" indicator |
| `Feedback/_Detail.cshtml` | 9 | `Model.IsAdmin` | Reporter shown as `<human-link>` (admin) vs name (user) |
| `Feedback/_Detail.cshtml` | 22 | `Model.IsAdmin` | Status / GH-issue-number / lock-icon controls |
| `Feedback/_Detail.cshtml` | 54 | `Model.IsAdmin` | Assignment selectors (assignee, team, unassigned-only checkbox) |

### Google Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Google/_SyncTabContent.cshtml` | 53 | `Model.CanExecuteActions && result.DriftCount > 0` | "Add All Missing" / "Sync All" bulk buttons |
| `Google/_SyncTabContent.cshtml` | 125 | `Model.CanViewAudit` | Per-resource Audit link |
| `Google/_SyncTabContent.cshtml` | 130 | `Model.CanExecuteActions && !diff.IsInSync && !hasError` | Per-resource "Add Missing" / "Sync All" buttons |

### Calendar Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Calendar/Event.cshtml` | 18 | `Model.CanEdit` | Edit / Delete buttons in event header |
| `Calendar/Event.cshtml` | 96 | `Model.CanEdit && o.IsRecurring && o.OriginalOccurrenceStartUtc is not null` | Per-occurrence Edit / Cancel-this buttons |

### Application / Governance Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Application/Index.cshtml` | 36 | `Model.IsApprovedColaborador && Model.CanSubmitNew` | Asociado upgrade hint + link |
| `Application/Details.cshtml` | 54 | `Model.CanWithdraw` | Withdraw form |
| `Application/ApplicationDetail.cshtml` | 75 | `Model.CanApproveReject` | Board voting link card |
| `Governance/Index.cshtml` | 98 | `Model.CanApply` | Apply button (in rejected branch) |
| `Governance/Index.cshtml` | 158 | `Model.IsApprovedColaborador && Model.CanApply` | Asociado upgrade card |

### Shared Components

| View | Line | Check | Controls |
|---|---|---|---|
| `Shared/Components/ProfileCard/Default.cshtml` | 132 | `Model.CanSendMessage` | "Send Message" button |
| `Shared/Components/ProfileCard/Default.cshtml` | 202 | `Model.CanViewLegalName` | Board / private information card |
| `Shared/_ShiftsSummaryCard.cshtml` | 39 | `Model.CanManageShifts` | "Manage Shifts" link |
| `Shared/_HumanPopover.cshtml` | 7 | `Model.IsSuspended` | Suspended badge in popover |
| `Shared/_BuildStrikeRotaTable.cshtml` | 67, 111, 140, 159, 191, 210 | `Model.ShowSignups` | Signup column header + cells + colspan adjustments |
| `Shared/_EventRotaTable.cshtml` | 42, 69, 110 | `Model.ShowSignups` | Signup column header + cells + colspan adjustments |
| `AuthorizeViewTagHelper` | — | `IAuthorizationService.AuthorizeAsync(user, Policy)` | Backs every `authorize-policy="..."` attribute above |

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
