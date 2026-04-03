# Authorization Inventory

Phase 0 of the [first-class authorization transition plan](plans/2026-04-03-first-class-authorization-transition.md).

Generated 2026-04-03. Covers every `[Authorize(Roles)]`, `RoleChecks.*`, `ShiftRoleChecks.*`, and auth-related `Model.*` usage.

---

## 1. Controller Authorization Map

### Admin Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `AdminController` | Class | `Admin` | `RoleNames.Admin` |
| `AdminController.Version` | Action | `AllowAnonymous` | Override |
| `AdminDuplicateAccountsController` | Class | `Admin` | `RoleNames.Admin` |
| `AdminMergeController` | Class | `Admin` | `RoleNames.Admin` |
| `AdminLegalDocumentsController` | Class | `Board, Admin` | `RoleGroups.BoardOrAdmin` |
| `EmailController` | Class | `Admin` | `RoleNames.Admin` |

### Google Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `GoogleController` | Class | `Admin` | `RoleNames.Admin` |
| `GoogleController.ResyncTeam` | Action | `TeamsAdmin, Board, Admin` | `RoleGroups.TeamsAdminBoardOrAdmin` |
| `GoogleController.DeleteTeamResources` | Action | `TeamsAdmin, Board, Admin` | `RoleGroups.TeamsAdminBoardOrAdmin` |
| `GoogleController.AccountLinkingReport` | Action | `Board, Admin` | `RoleGroups.BoardOrAdmin` |
| `GoogleController.AccountLinkingReportData` | Action | `Board, Admin` | `RoleGroups.BoardOrAdmin` |
| `GoogleController.AdminProvisionEmail` | Action | `HumanAdmin, Board, Admin` | `RoleGroups.HumanAdminBoardOrAdmin` |
| `GoogleController.AdminLinkEmail` | Action | `HumanAdmin, Admin` | `RoleGroups.HumanAdminOrAdmin` |

### Tickets Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `TicketController` | Class | `TicketAdmin, Admin, Board` | `RoleGroups.TicketAdminBoardOrAdmin` |
| `TicketController.MatchUser` | Action | `TicketAdmin, Admin` | `RoleGroups.TicketAdminOrAdmin` |
| `TicketController.TriggerSync` | Action | `Admin` | `RoleNames.Admin` |
| `TicketController.UnmatchUser` | Action | `TicketAdmin, Admin` | `RoleGroups.TicketAdminOrAdmin` |
| `TicketController.MatchAllByEmail` | Action | `TicketAdmin, Admin` | `RoleGroups.TicketAdminOrAdmin` |

### Campaigns Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CampaignController` | Class | `[Authorize]` (authenticated) | — |
| `CampaignController.Create` (GET/POST) | Action | `Admin` | `RoleNames.Admin` |
| `CampaignController.Edit` (GET/POST) | Action | `Admin` | `RoleNames.Admin` |
| `CampaignController.Detail` | Action | `TicketAdmin, Admin` | `RoleGroups.TicketAdminOrAdmin` |
| `CampaignController.ImportCodes` | Action | `Admin` | `RoleNames.Admin` |
| `CampaignController.GenerateCodes` | Action | `TicketAdmin, Admin` | `RoleGroups.TicketAdminOrAdmin` |
| `CampaignController.DeleteCode` | Action | `Admin` | `RoleNames.Admin` |
| `CampaignController.SendWave` | Action | `Admin` | `RoleNames.Admin` |
| `CampaignController.Activate/Complete/Reactivate` | Action | `Admin` | `RoleNames.Admin` |
| `CampaignController.AssignManual` | Action | `Admin` | `RoleNames.Admin` |
| `CampaignController.UnassignCode` | Action | `Admin` | `RoleNames.Admin` |
| `CampaignController.ResendGrant` | Action | `Admin` | `RoleNames.Admin` |

### Finance Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `FinanceController` | Class | `FinanceAdmin, Admin` | `RoleGroups.FinanceAdminOrAdmin` |

### Budget Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `BudgetController` | Class | `[Authorize]` (authenticated) | — |
| Runtime guards | In-method | `RoleChecks.IsFinanceAdmin(User)` | Controls edit vs read-only |

### Board Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `BoardController` | Class | `Board, Admin` | `RoleGroups.BoardOrAdmin` |

### Onboarding Review Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `OnboardingReviewController` | Class | `ConsentCoordinator, VolunteerCoordinator, Board, Admin` | `RoleGroups.ReviewQueueAccess` |
| `OnboardingReviewController.PerformConsentCheck` (GET/POST) | Action | `ConsentCoordinator, Board, Admin` | `RoleGroups.ConsentCoordinatorBoardOrAdmin` |
| `OnboardingReviewController.RejectProfile` | Action | `ConsentCoordinator, Board, Admin` | `RoleGroups.ConsentCoordinatorBoardOrAdmin` |
| `OnboardingReviewController.ApproveApplication` | Action | `Board, Admin` | `RoleGroups.BoardOrAdmin` |
| `OnboardingReviewController.RejectApplication` | Action | `Board, Admin` | `RoleGroups.BoardOrAdmin` |
| `OnboardingReviewController.CastVote` | Action | `Board` | `RoleNames.Board` |
| `OnboardingReviewController.FinalizeVote` | Action | `Board, Admin` | `RoleGroups.BoardOrAdmin` |

### Governance / Application Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `GovernanceController` | Class | `[Authorize]` (authenticated) | — |
| `GovernanceController.Roles` | Action | `Board, Admin` | `RoleGroups.BoardOrAdmin` |
| `ApplicationController` | Class | `[Authorize]` (authenticated) | — |
| `ApplicationController.Approve/Reject` | Action | `Board, Admin` | `RoleGroups.BoardOrAdmin` |

### Profile / Contacts Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ProfileController` | Class | `[Authorize]` (authenticated) | — |
| `ProfileController.AdminDetail` | Action | `HumanAdmin, Board, Admin` | `RoleGroups.HumanAdminBoardOrAdmin` |
| `ProfileController.AdminList` | Action | `HumanAdmin, Board, Admin` | `RoleGroups.HumanAdminBoardOrAdmin` |
| `ProfileController.Suspend/Unsuspend` | Action | `HumanAdmin, Board, Admin` | `RoleGroups.HumanAdminBoardOrAdmin` |
| `ProfileController.ApproveProfile/RejectProfile` | Action | `HumanAdmin, Board, Admin` | `RoleGroups.HumanAdminBoardOrAdmin` |
| `ProfileController.DowngradeTier/OverrideTier` | Action | `HumanAdmin, Board, Admin` | `RoleGroups.HumanAdminBoardOrAdmin` |
| `ProfileController.AddRole/RemoveRole` | Action | `HumanAdmin, Board, Admin` | `RoleGroups.HumanAdminBoardOrAdmin` |
| `ProfileController.Roles` | Action | `HumanAdmin, Board, Admin` | `RoleGroups.HumanAdminBoardOrAdmin` |
| `ProfileController.ExportCsv` | Action | `HumanAdmin, Board, Admin` | `RoleGroups.HumanAdminBoardOrAdmin` |
| `ContactsController` | Class | `HumanAdmin, Board, Admin` | `RoleGroups.HumanAdminBoardOrAdmin` |

### Teams Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `TeamController` | Class | `[Authorize]` (authenticated) | — |
| `TeamController.Index/Details` | Action | `AllowAnonymous` | Override |
| `TeamController.Create` (GET/POST) | Action | `TeamsAdmin, Board, Admin` | `RoleGroups.TeamsAdminBoardOrAdmin` |
| `TeamController.Edit/Delete` | Action | `TeamsAdmin, Board, Admin` | `RoleGroups.TeamsAdminBoardOrAdmin` |
| `TeamController.Archive` | Action | `Board, Admin` | `RoleGroups.BoardOrAdmin` |
| `TeamController.ManageGoogleResources` | Action | `TeamsAdmin, Board, Admin` | `RoleGroups.TeamsAdminBoardOrAdmin` |
| `TeamAdminController` | Class | `[Authorize]` (authenticated) | Coordinator checks at runtime |

### Camps Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CampController` | None (no class `[Authorize]`) | Some actions `AllowAnonymous` | Camp lead + CampAdmin runtime checks |
| `CampAdminController` | Class | `CampAdmin, Admin` | `RoleGroups.CampAdminOrAdmin` |
| `CampAdminController.Delete` | Action | `Admin` | `RoleNames.Admin` |

### Shifts Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ShiftsController` | Class | `[Authorize]` (authenticated) | — |
| `ShiftsController.Settings` (GET/POST) | Action | `Admin` | `RoleNames.Admin` |
| `ShiftAdminController` | Class | `[Authorize]` (authenticated) | Coordinator checks at runtime |
| `ShiftDashboardController` | Class | `Admin, NoInfoAdmin, VolunteerCoordinator` | Inline role string |

### Volunteer Management Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `VolController` | Class | `[Authorize]` (authenticated) | — |
| `VolController.Settings` (GET/POST) | Action | `Admin` | `RoleNames.Admin` |
| Runtime guards (Management, Dashboard, etc.) | In-method | `ShiftRoleChecks.CanAccessDashboard(User)` | `Admin \| NoInfoAdmin \| VolunteerCoordinator` |

### Feedback Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `FeedbackController` | Class | `[Authorize]` (authenticated) | — |
| `FeedbackController.Respond/UpdateStatus` | Action | `FeedbackAdmin, Admin` | `RoleGroups.FeedbackAdminOrAdmin` |

### Other

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ConsentController` | Class | `[Authorize]` (authenticated) | — |
| `NotificationController` | Class | `[Authorize]` (authenticated) | — |
| `HumanRedirectController` | Class | `[Authorize]` (authenticated) | — |
| `ProfileApiController` | Class | `[Authorize]` (authenticated) | — |
| `LegalController` | Class | `AllowAnonymous` | — |
| `CampApiController` | Class | `AllowAnonymous` | — |
| `ColorPaletteController` | Class | `AllowAnonymous` | — |
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

---

## 3. Same-Rule-Different-Spelling Table

These entries express the same authorization rule using different syntax across controllers and views.

| Rule | Controller Spelling | View Spelling |
|---|---|---|
| Admin only | `[Authorize(Roles = RoleNames.Admin)]` | `RoleChecks.IsAdmin(User)` |
| Board or Admin | `[Authorize(Roles = RoleGroups.BoardOrAdmin)]` | `RoleChecks.IsAdminOrBoard(User)` |
| TeamsAdmin/Board/Admin | `[Authorize(Roles = RoleGroups.TeamsAdminBoardOrAdmin)]` | `RoleChecks.IsTeamsAdminBoardOrAdmin(User)` |
| TicketAdmin/Board/Admin | `[Authorize(Roles = RoleGroups.TicketAdminBoardOrAdmin)]` | `RoleChecks.CanAccessTickets(User)` |
| TicketAdmin or Admin | `[Authorize(Roles = RoleGroups.TicketAdminOrAdmin)]` | `RoleChecks.CanManageTickets(User)` |
| CampAdmin or Admin | `[Authorize(Roles = RoleGroups.CampAdminOrAdmin)]` | `RoleChecks.IsCampAdmin(User)` |
| HumanAdmin/Board/Admin | `[Authorize(Roles = RoleGroups.HumanAdminBoardOrAdmin)]` | `RoleChecks.IsHumanAdminBoardOrAdmin(User)` |
| FeedbackAdmin or Admin | `[Authorize(Roles = RoleGroups.FeedbackAdminOrAdmin)]` | `RoleChecks.IsFeedbackAdmin(User)` |
| FinanceAdmin or Admin | `[Authorize(Roles = RoleGroups.FinanceAdminOrAdmin)]` | `RoleChecks.IsFinanceAdmin(User)` / `RoleChecks.CanAccessFinance(User)` |
| Review queue access | `[Authorize(Roles = RoleGroups.ReviewQueueAccess)]` | `RoleChecks.CanAccessReviewQueue(User)` |
| Shift dashboard access | `[Authorize(Roles = "Admin,NoInfoAdmin,VolunteerCoordinator")]` | `ShiftRoleChecks.CanAccessDashboard(User)` |
| Privileged signup approver | `ShiftRoleChecks.IsPrivilegedSignupApprover(User)` (runtime) | `Model.CanApproveSignups` (view model) |
| Volunteer manager | `RoleChecks.IsVolunteerManager(User)` (runtime) | `RoleChecks.CanAccessVolunteers(User)` (nav, broader) |

---

## 4. Enforcement Gaps

### View-Only (button hidden, no server-side attribute guard)

| Location | Check | Risk |
|---|---|---|
| `Vol/Management.cshtml` — "Create Department" | `RoleChecks.IsAdmin(User)` in view | `VolController.Management` is `[Authorize]` (any authenticated user). The POST create action has runtime checks but no `[Authorize(Roles)]` attribute. |
| `Vol/_VolLayout.cshtml` — Dashboard tab | `ShiftRoleChecks.CanAccessDashboard(User)` in view | Dashboard GET actions guard with `if (!ShiftRoleChecks.CanAccessDashboard(User))` at runtime, not via attribute. |
| `Vol/_VolLayout.cshtml` — Settings tab | `RoleChecks.IsAdmin(User)` in view | Settings action has `[Authorize(Roles = RoleNames.Admin)]` attribute — **OK, enforced server-side**. |
| `CampAdmin/Index.cshtml` — "Delete Camp" | `RoleChecks.IsAdmin(User)` in view | Delete action has `[Authorize(Roles = RoleNames.Admin)]` — **OK, narrower than class-level CampAdminOrAdmin**. |
| `Team/Summary.cshtml` — Edit/Delete/Archive links | `RoleChecks.IsAdminOrBoard(User)` in view | Team edit actions have `[Authorize(Roles = RoleGroups.TeamsAdminBoardOrAdmin)]` — view is **stricter** than server (hides from TeamsAdmin). |
| `Ticket/Index.cshtml` — "Event Settings" link | `RoleChecks.IsAdmin(User)` in view | Targets `Shifts/Settings` which has `[Authorize(Roles = RoleNames.Admin)]` — **OK**. |

### Server-Only (protected endpoint, no visible UI gating)

| Endpoint | Roles | Note |
|---|---|---|
| `GoogleController` most actions | `Admin` (class-level) | Class-level `[Authorize(Roles = Admin)]` is enforced as AND with action-level attributes — only Admin can access any action. The broader action-level roles (e.g., `TeamsAdminBoardOrAdmin` on `ResyncTeam`) are effectively dead code since Admin is always required. Whether this is intentional or a bug to investigate in Phase 1 should be confirmed. |
| `ProfileController.ExportCsv` | `HumanAdminBoardOrAdmin` | No visible button in AdminList view (accessed via URL pattern). |

### Runtime-Only Guards (no attribute, enforced in method body)

These actions rely on `if` checks + early return/forbid instead of `[Authorize(Roles)]`:

| Controller | Action | Guard |
|---|---|---|
| `VolController` | `Dashboard`, `StaffingSummary`, `WeeklySummary`, `AvailabilitySummary` | `ShiftRoleChecks.CanAccessDashboard(User)` |
| `ShiftAdminController` | Various | Coordinator-of-team check via `HumansTeamControllerBase` |
| `TeamAdminController` | Various | Coordinator-of-team check via `HumansTeamControllerBase` |
| `BudgetController` | Various | `RoleChecks.IsFinanceAdmin(User)` for edit operations |
| `FeedbackController` | `Index`, `Detail` | `RoleChecks.IsFeedbackAdmin(User)` to determine admin vs user view |

---

## 5. Canonical Policy Name Table

These are the named ASP.NET policies that Phase 1 will register. Each maps from the current authorization dialect(s) to a single canonical name.

| Canonical Policy Name | Roles | Current Sources |
|---|---|---|
| `AdminOnly` | Admin | `RoleNames.Admin`, `RoleChecks.IsAdmin` |
| `BoardOrAdmin` | Board, Admin | `RoleGroups.BoardOrAdmin`, `RoleChecks.IsAdminOrBoard` |
| `HumanAdminBoardOrAdmin` | HumanAdmin, Board, Admin | `RoleGroups.HumanAdminBoardOrAdmin`, `RoleChecks.IsHumanAdminBoardOrAdmin` |
| `HumanAdminOrAdmin` | HumanAdmin, Admin | `RoleGroups.HumanAdminOrAdmin` |
| `TeamsAdminBoardOrAdmin` | TeamsAdmin, Board, Admin | `RoleGroups.TeamsAdminBoardOrAdmin`, `RoleChecks.IsTeamsAdminBoardOrAdmin` |
| `CampAdminOrAdmin` | CampAdmin, Admin | `RoleGroups.CampAdminOrAdmin`, `RoleChecks.IsCampAdmin` |
| `TicketAdminBoardOrAdmin` | TicketAdmin, Admin, Board | `RoleGroups.TicketAdminBoardOrAdmin`, `RoleChecks.CanAccessTickets` |
| `TicketAdminOrAdmin` | TicketAdmin, Admin | `RoleGroups.TicketAdminOrAdmin`, `RoleChecks.CanManageTickets` |
| `FeedbackAdminOrAdmin` | FeedbackAdmin, Admin | `RoleGroups.FeedbackAdminOrAdmin`, `RoleChecks.IsFeedbackAdmin` |
| `FinanceAdminOrAdmin` | FinanceAdmin, Admin | `RoleGroups.FinanceAdminOrAdmin`, `RoleChecks.IsFinanceAdmin`, `RoleChecks.CanAccessFinance` |
| `ReviewQueueAccess` | ConsentCoordinator, VolunteerCoordinator, Board, Admin | `RoleGroups.ReviewQueueAccess`, `RoleChecks.CanAccessReviewQueue` |
| `ConsentCoordinatorBoardOrAdmin` | ConsentCoordinator, Board, Admin | `RoleGroups.ConsentCoordinatorBoardOrAdmin` |
| `ShiftDashboardAccess` | Admin, NoInfoAdmin, VolunteerCoordinator | Inline role string on `ShiftDashboardController`, `ShiftRoleChecks.CanAccessDashboard` |
| `ShiftDepartmentManager` | Admin, NoInfoAdmin, VolunteerCoordinator | `ShiftRoleChecks.CanManageDepartment` |
| `PrivilegedSignupApprover` | Admin, NoInfoAdmin | `ShiftRoleChecks.IsPrivilegedSignupApprover` |
| `VolunteerManager` | Admin, VolunteerCoordinator | `RoleChecks.IsVolunteerManager` |
| `VolunteerSectionAccess` | TeamsAdmin, Board, Admin, VolunteerCoordinator | `RoleChecks.CanAccessVolunteers` |
| `ActiveMemberOrShiftAccess` | ActiveMember claim OR ShiftDashboardAccess | Composite: nav-level Shifts visibility |
| `MedicalDataViewer` | Admin, NoInfoAdmin | `ShiftRoleChecks.CanViewMedical` |

### Notes on Policy Design

- `ShiftDashboardAccess` and `ShiftDepartmentManager` currently resolve to the same roles but are semantically distinct. Keeping them separate allows future divergence.
- `ActiveMemberOrShiftAccess` is a composite policy that checks the `ActiveMember` claim OR falls back to role-based shift access. This will need a custom `IAuthorizationRequirement` + handler rather than a simple `RequireRole`.
- `MedicalDataViewer` is a data-access policy, not a page-access policy. It controls whether medical fields are visible within pages the user already has access to.
- Object-relative policies (coordinator of specific team, camp lead of specific camp) are Phase 2 candidates and are NOT included in this table. Phase 1 covers only coarse-grained role-based policies.

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
| `IsBoard` | (no standalone policy — Board alone is rarely the gate) |
| `IsAdminOrBoard` | `BoardOrAdmin` |
| `IsTeamsAdminBoardOrAdmin` | `TeamsAdminBoardOrAdmin` |
| `IsCampAdmin` | `CampAdminOrAdmin` |
| `CanAccessReviewQueue` | `ReviewQueueAccess` |
| `CanAccessTickets` | `TicketAdminBoardOrAdmin` |
| `CanManageTickets` | `TicketAdminOrAdmin` |
| `IsHumanAdminBoardOrAdmin` | `HumanAdminBoardOrAdmin` |
| `IsHumanAdmin` | (no standalone policy — used only for standalone HumanAdmin nav case) |
| `IsFeedbackAdmin` | `FeedbackAdminOrAdmin` |
| `IsFinanceAdmin` / `CanAccessFinance` | `FinanceAdminOrAdmin` |
| `IsVolunteerManager` | `VolunteerManager` |
| `CanAccessVolunteers` | `VolunteerSectionAccess` |
| `BypassesMembershipRequirement` | (filter-level, not a page policy) |
| `GetAssignableRoles` / `CanManageRole` | (scoped logic, not a simple policy) |

### ShiftRoleChecks Methods → Canonical Policy Mapping

| Method | Canonical Policy |
|---|---|
| `IsPrivilegedSignupApprover` | `PrivilegedSignupApprover` |
| `CanManageDepartment` | `ShiftDepartmentManager` |
| `CanAccessDashboard` | `ShiftDashboardAccess` |
| `CanViewMedical` | `MedicalDataViewer` |
