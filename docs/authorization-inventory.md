# Authorization Inventory

Originally produced as Phase 0 of the first-class authorization transition plan (historical; the plan doc has since been pruned). **Phase 1 is complete:** every canonical policy in §5 is registered in `AuthorizationPolicyExtensions.AddHumansAuthorizationPolicies`, all controllers (including the Events Guide section, which now uses `[Authorize(Policy = PolicyNames.EventsAdminOrAdmin)]`) use `[Authorize(Policy = PolicyNames.X)]`, the `authorize-policy` TagHelper resolves through `IAuthorizationService`, and views no longer call `RoleChecks.*` / `ShiftRoleChecks.*` directly. **Phase 2 (resource-based authorization)** has shipped multiple vertical slices — see §6 (`TeamAuthorizationHandler`, `CampAuthorizationHandler`, `BudgetAuthorizationHandler`, `RoleAssignmentAuthorizationHandler`, `ContainerAuthorizationHandler`, `ExpenseReportAuthorizationHandler`, `IbanAccessHandler`, `StoreOrderAuthorizationHandler`, `UserEmailAuthorizationHandler`, `IssuesAuthorizationHandler`, `AgentRateLimitHandler`). **Phase 3 (service-layer enforcement) is cancelled.**

Generated 2026-04-03. Refreshed 2026-06-24 (via `/freshness-sweep`; full re-scan against worktree `freshness-sweep-2026-06-23T231018Z`: `SepaReopen` / `SepaGenerate` actions and `ExpenseReportOperation.{IncludeInSepaPayout,ReopenSepa}` are not present in this worktree's code — removed from §1, §3, §6, and the §6 call-site table; `ExpensesController` `AuthorizeAsync` call-site line numbers corrected to 206, 589, 635, 655, 700, 721; all other entries verified unchanged). Previously refreshed 2026-06-13 (via `/freshness-sweep`; `GovernanceBoardVotingController.Finalize` (POST, `AdminOnly`) added — action-level override finalizing board vote rounds; `VolunteerTrackingController.SetAvailabilityDay` / `ClearAvailabilityDay` added (`VolunteerTrackingWrite`); `ProfileController.BuildSentMessagesContextAsync` new `AuthorizeAsync(User, PolicyNames.PrivilegedSignupApprover)` call at line 1911 added to §6 call site table; line numbers in §6 call site table updated for `ExpensesController`, `ProfileController`, and `UsersAdminController` to reflect current source). Previously refreshed 2026-06-12 (via `/freshness-sweep`; catch-up: `ToggleCampFavourite` (route `/Events/Barrio/{slug}/Favourite/{eventId:guid}`) was renamed to `ToggleCardFavourite` (route `/Events/Card/Favourite/{eventId:guid}`) in #925 — whole-event favourite from the events card, now camp-and-profile-page-agnostic (missed by the 2026-06-10 sweep); #982 added `ExpensesController.SepaReopen` (`[HttpPost("{id:guid}/Sepa/Reopen")]`, `[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]` + resource-based `ExpenseReportOperation.ReopenSepa`) — reopens a SepaSent report back to Approved; `ExpenseReportAuthorizationHandler` now covers `ReopenSepa` for FinanceAdmin/Admin; #985 and #986 are service-layer and view-only changes with no new authorization surface). Previously refreshed 2026-06-10 (via `/freshness-sweep`; #884 added the Survey section — `SurveyController` (`[AllowAnonymous]` — public wizard answering flow), `SurveyAdminController` (`BoardOrAdmin` — authoring/send/results), and `SurveysApiController` (`SurveyApiKeyAuthFilter` — key-authed agent read API); #931 added `ICalFeedApiController` (`[AllowAnonymous]` with secret-in-URL token at `/api/ical/{userId}/{token}.ics`); #930 added `AccountController.GateLogin` (GET/POST, no `[Authorize]` — shared kiosk credential for the gate terminal) and `TicketsGateAdminController` (`TicketAdminOrAdmin` — gate credential management at `/Tickets/Admin/Gate`); `ScannerController`'s class-level policy is now `PolicyNames.ScannerAccess` (a composite assertion that also admits the gate-terminal shared account by well-known id, not just TicketAdmin/Board/Admin roles) — the `ScannerAccess` policy is now listed in §5). Previously refreshed 2026-06-09 (via `/freshness-sweep`, full re-scan; #900 expense travel lines + personal IOU view reshaped the Expenses guard surface — the `ExpenseReportOperationRequirement` resource handler now covers `View` (Detail/Attachment), `Endorse`, `CoordinatorReject`, `Approve`, `FinanceReject`, and `IncludeInSepaPayout`, while all submitter-side actions (Edit, line CRUD including the new `AddMileage`/`AddPerDiem`, Submit, Withdraw, Iban) are gated by inline owner checks (`report.SubmitterUserId != user.Id → Forbid()`); #916 added the barcode scan & search actions (`Barcode`, `Tickets`, `Tickets/Card`) to `ScannerController` and the authenticated `search` / `by-userid` endpoints to `ProfileApiController`; `UsersAdminController` is now gated by a single class-level `HumanAdminBoardOrAdmin` policy with `AdminOnly` action overrides (`RevealIban`, `Audience`, `PurgeHuman`); `TeamController.EditTeam` (POST) gained an in-method `AdminOnly` check driving the `IsSensitive` leave-unchanged guard; the Store landing page admin button group is the first view spelling of `StoreCatalogAdmin`; `_HumanPopover` gained an `AnyAdminRole` camp-visibility flag and the Admin dashboard activity panels an `AdminOnly` view gate). Previously refreshed 2026-06-07 (via `/freshness-sweep`; #899 account-merge consolidation deleted `AdminMergeController` + `AdminDuplicateAccountsController` and replaced them with the single `UsersAdminAccountMergesController` at `/Users/Admin/AccountMerges`; #901 admin route moves gutted `AdminController` down to just the `/Admin` dashboard tile and relocated routes into section controllers — `PurgeHuman` and `RevealIban` now live on `UsersAdminController`, the debug/diagnostics routes were absorbed by `DebugController`, and the role-assignment `AddRole`/`EndRole` guards moved from `ProfileController` to `UsersAdminController`; #898 added the read-only `ShiftsController.Summary*` actions gated by `ShiftDepartmentManager`; #881 name-only access deleted the `IsActiveMember` / `ActiveMemberOrShiftAccess` requirement+handler pairs and `RoleChecks.BypassesMembershipRequirement` — `MembershipRequiredFilter` now gates the app purely on the stored `UserState` (only `Active` reaches it) and routes the rest to name-entry / status-wall / cancel-deletion landings on the new `UserController`). Previously refreshed 2026-06-05 (adds the `CampComplianceAccess` policy + `CampComplianceAccessHandler` and the new `CampComplianceController` for the read-only Barrios compliance matrix, split out of `CampAdminController` so it can be gated more broadly than CampAdmin-only — #894; `RoleAssignmentClaimsTransformation` was re-sourced from `IRoleAssignmentRepository` to `IRoleAssignmentService` in #889, an internal sourcing change with no inventory impact). Previously refreshed 2026-06-04 (full re-scan via `/freshness-sweep`; adds `ProfileApiController.BurnerNameCount`, `ShiftsController.ToggleDay`, the `StoreAdminController` Payments/reconcile actions, and the global `NameRequiredFilter` action filter). Previously refreshed 2026-06-03 (the new `RoleNames.EETeamAdmin` cross-team role, the `TeamOperationRequirement.ManageEarlyEntry` resource operation + `TeamAdminController` EarlyEntry actions, and the build-hash tooltip re-gated to `AdminOnly` / FullAdmin in commit 3c6a878e). Covers every `[Authorize(Policy)]` / `[Authorize(Roles)]` attribute on controllers and actions in `src/Humans.Web/Controllers/` (including `Controllers/Api/` and `Controllers/Mailer/`), every `RoleChecks.*` / `ShiftRoleChecks.*` invocation across `src/Humans.Web/` and `src/Humans.Application/`, every `IAuthorizationService.AuthorizeAsync` call site, every `authorize-policy` TagHelper attribute and `User.IsInRole` / `Model.X` authorization check across `src/Humans.Web/Views/` and `src/Humans.Web/ViewComponents/`, and every `AuthorizationHandler<T, R>` (and `IAuthorizationHandler`) under `src/Humans.Web/Authorization/` and `src/Humans.Application/Authorization/`.

The `Source` column reflects the constant referenced in the attribute as it appears in the code today.

---

## 1. Controller Authorization Map

### Admin Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `AdminController` | Class | `[Route("Admin")]` only — no class-level `[Authorize]` | — |
| `AdminController.Index` | Action | `Admin, Board, HumanAdmin, TeamsAdmin, CampAdmin, TicketAdmin, EventsAdmin, FeedbackAdmin, FinanceAdmin, StoreAdmin, CantinaAdmin, NoInfoAdmin, VolunteerCoordinator, ConsentCoordinator` | `PolicyNames.AnyAdminRole` (the only action left on the gutted dashboard controller — #901) |
| `UsersAdminController.PurgeHuman` | Action | `Admin` | `PolicyNames.AdminOnly` (override on the class-level `HumanAdminBoardOrAdmin` controller — see Profile / Contacts section; moved off `ProfileController` in #901) |
| `DebugController.Logs` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `DebugController.Maintenance` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `DebugController.Configuration` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `DebugController.DbVersion` | Action | `AllowAnonymous` | Override |
| `DebugController.DbStats` / `ResetDbStats` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `DebugController.ClearHangfireLocks` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `DebugController.CacheStats` / `ResetCacheStats` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `DebugController.ClientStats` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `DebugController.FormatGallery` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `UsersAdminController.Audience` | Action | `Admin` | `PolicyNames.AdminOnly` (override on the class-level `HumanAdminBoardOrAdmin` controller) |
| `AdminAgentController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `UsersAdminAccountMergesController` | Class | `Admin` | `PolicyNames.AdminOnly` (consolidated account-merge surface at `/Users/Admin/AccountMerges`; replaced the deleted `AdminMergeController` + `AdminDuplicateAccountsController` — #899; all actions — `Index`, `Merge`, `MergeRequest`, `Dismiss`, `Close` — inherit) |
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
| `TicketsOnsiteAdminController` | Class | `TicketAdmin, Admin, Board` OR the gate-terminal shared account (by well-known id) | `PolicyNames.ScannerAccess` (gate staff check the onsite roster from the door alongside the scanner) |
| `TicketsGateAdminController` | Class | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` (gate credential management at `/Tickets/Admin/Gate` — `Index` GET, `SetPassword` POST both inherit) |

### Scanner Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ScannerController` | Class | `TicketAdmin, Admin, Board` OR the gate-terminal shared account (by well-known id) | `PolicyNames.ScannerAccess` (composite assertion — also admits `SystemUserIds.GateTerminal` by NameIdentifier claim so the shared kiosk session can scan without holding any role; all actions inherit — `Index`, `Barcode`, `Tickets`, `Tickets/Card`) |

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
| `ExpensesController` runtime guards | In-method | `authService.AuthorizeAsync(User, report, new ExpenseReportOperationRequirement(ExpenseReportOperation.X))` — `View` (Detail + Attachment), `Endorse`, `CoordinatorReject`, `Approve`, `FinanceReject` | Resource-based (see §6) |
| `ExpensesController` owner guards | In-method | Submitter-side actions (`Edit` GET/POST, `AddLine`, `AddMileage`, `AddPerDiem`, `UpdateLine`, `RemoveLine`, `AttachFile`, `RemoveAttachment`, `Submit`, `Withdraw`, `Iban` GET/POST) gate on `report.SubmitterUserId != user.Id → Forbid()` (13 sites — travel lines added in #900) | Inline owner check |

### Store Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `StoreController` | Class | `[Authorize]` (authenticated) | — |
| `StoreController` runtime guards | In-method | `authService.AuthorizeAsync(User, order, StoreOrderOperationRequirement.{View, AddLine, RemoveLine, EditCounterparty, Pay, Delete})` for existing orders and `StoreOrderCreateContext` for Create (both camp orders via `Create` and team orders via `CreateTeamOrder`). Index also seeds `isPrivilegedReader = RoleChecks.CanAdministerStore(User) \|\| RoleChecks.IsTeamsAdmin(User)` (PR #845). | Resource-based (see §6) |
| `StoreAdminController` | Class | `StoreAdmin, FinanceAdmin, Admin` | `PolicyNames.StoreCatalogAdmin` |
| `StoreAdminController.Payments` | Action | `StoreAdmin, FinanceAdmin, Admin` inherited (`[HttpGet("Payments")]`) | `PolicyNames.StoreCatalogAdmin` (Stripe ↔ Store ledger reconciliation report) |
| `StoreAdminController.RecordMissingPayments` | Action | `StoreAdmin, FinanceAdmin, Admin` inherited (`[HttpPost("Payments/RecordMissing")]`) | `PolicyNames.StoreCatalogAdmin` (records missing Stripe payments) |
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
| `GovernanceBoardVotingController.Finalize` | Action | `Admin` | `PolicyNames.AdminOnly` (POST `/Governance/BoardVoting/Finalize`; action-level override on class-level `BoardOrAdmin` — finalizes a board vote round by recording the meeting date and triggering application decisions) |

### Profile / Contacts Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ProfileController` | Class | `[Authorize]` (authenticated) | — |
| `ProfileController.VerifyEmail` | Action | `AllowAnonymous` | Override |
| `ProfileController.Picture` | Action | `AllowAnonymous` | Override |
| `ProfileController.PublicPopover` | Action | `AllowAnonymous` | Override (`[HttpGet("{id:guid}/PublicPopover")]`; 404s unless target is a coordinator on a public-page team) |
| `ProfileController.AdminAddVerifiedEmail` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `ProfileController.AdminVerifyEmail` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `UsersAdminController` | Class | `HumanAdmin, Board, Admin` | `PolicyNames.HumanAdminBoardOrAdmin` (class-level — `AdminList`, `Roles`, `AdminDetail`, `AdminOutbox`, `SuspendHuman`, `UnsuspendHuman`, `RejectSignup`, `AddRole` GET/POST, `EndRole` all inherit) |
| `UsersAdminController.RevealIban` | Action | `Admin` | `PolicyNames.AdminOnly` (override; `Audience` and `PurgeHuman` are the other `AdminOnly` overrides — listed under Admin section) |
| `UsersAdminController.AddRole/EndRole` runtime guards | In-method | `_authorizationService.AuthorizeAsync(User, roleName, RoleAssignmentOperationRequirement.Manage)` | Resource-based (see §6) |
| `ProfileController` email-action runtime guards | In-method | `_authorizationService.AuthorizeAsync(User, userId, UserEmailOperations.Edit)` (gating 18 email-edit endpoints) | Resource-based (see §6) |
| `ProfileApiController` | Class | `[Authorize]` (authenticated) | — |
| `ProfileApiController.Search` | Action | `[Authorize]` inherited (`[HttpGet("search")]`) | — (people search; admin bit never set on this endpoint — #906/#916) |
| `ProfileApiController.BurnerNameCount` | Action | `[Authorize]` inherited (`[HttpGet("burner-name-count")]`) | — (excludes the authenticated viewer; self-exclusion uses session identity, not a caller-supplied id) |
| `ProfileApiController.GetByUserId` | Action | `[Authorize]` inherited (`[HttpGet("by-userid/{userId:guid}")]`) | — |

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
| `TeamController.EditTeam` (POST) runtime guard | In-method | `authorizationService.AuthorizeAsync(User, PolicyNames.AdminOnly)` — non-Admin editors post no `IsSensitive` value (checkbox is `authorize-policy="AdminOnly"`-suppressed), so the flag is passed as leave-unchanged unless the editor is a global Admin | `PolicyNames.AdminOnly` |
| `TeamAdminController` | Class | `[Authorize]` (authenticated) | Coordinator checks at runtime via `HumansTeamControllerBase` |
| `TeamAdminController` runtime guards (most actions) | In-method | `_authorizationService.AuthorizeAsync(User, team, TeamOperationRequirement.ManageCoordinators)` via `ResolveTeamManagementAsync` | Resource-based (see §6) |
| `TeamAdminController.EarlyEntry` / `AddEarlyEntry` / `EditEarlyEntry` / `RemoveEarlyEntry` / `LookupTicket` | In-method | `_authorizationService.AuthorizeAsync(User, team, TeamOperationRequirement.ManageEarlyEntry)` via `ResolveEarlyEntryManagementAsync` (Admin/TeamsAdmin/Board any team; EETeamAdmin any team; coordinator own team) | Resource-based (see §6) |

### Camps Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CampController` | Class | None at class level — anonymous public actions + `[Authorize]` per action | Camp lead + CampAdmin runtime checks |
| `CampController.Index` / `Details` / `SeasonDetails` | Action | `AllowAnonymous` | Override |
| `CampController.*` (Contact/Edit/Register/Members/Withdraw/Rejoin/AssignRole/UploadImage/etc.) | Action | `[Authorize]` (authenticated) | — |
| `CampController` runtime guards | In-method | `_authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` via `HumansCampControllerBase` | Resource-based (see §6) |
| `CampAdminController` | Class | `CampAdmin, Admin` | `PolicyNames.CampAdminOrAdmin` |
| `CampAdminController.Delete` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `CampComplianceController` | Class | `CampAdmin, Admin` OR any team/sub-team coordinator (custom handler) | `PolicyNames.CampComplianceAccess` (read-only Barrios compliance matrix at `/Barrios/Admin/Compliance`; split from `CampAdminController` so coordinators can view role staffing — #894) |
| `CampApiController` | Class | `AllowAnonymous` (with `BarriosPublic` CORS) | — |
| `ContainerController` | Class | `[Authorize]` (authenticated) | — |
| `ContainerController` runtime guards | In-method | `_authorizationService.AuthorizeAsync(User, target, ContainerOperationRequirement.{Manage, Place})` | Resource-based (see §6) |

### Shifts Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ShiftsController` | Class | `[Authorize]` (authenticated) | — |
| `ShiftsController.ToggleDay` | Action | `[Authorize]` inherited (`[HttpPost("ToggleDay")]`, `[ValidateAntiForgeryToken]`) | — (self-service day-rota toggle; name/dietary gates short-circuit) |
| `ShiftsController.Summary` / `SummaryTeam` / `SummaryRota` | Action | `Admin, NoInfoAdmin, VolunteerCoordinator` OR any team manager/coordinator | `PolicyNames.ShiftDepartmentManager` (read-only Shift Summary by Camp at `/Shifts/Summary` — #898) |
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
| `VolunteerTrackingController.SetCampSetup` / `ClearCampSetup` / `SetDayOff` / `ClearDayOff` / `SetAvailabilityDay` / `ClearAvailabilityDay` | Action | `Admin, VolunteerCoordinator` | `PolicyNames.VolunteerTrackingWrite` |

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

### Survey Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `SurveyController` | Class | `AllowAnonymous` | — (public survey answering wizard — invited token path `/Survey/Answer?t=…` and public slug path `/Survey/{slug}`; identity comes from the token's invitation, never the current principal; all actions inherit `[AllowAnonymous]`) |
| `SurveyAdminController` | Class | `Board, Admin` | `PolicyNames.BoardOrAdmin` (survey authoring at `/Survey/Admin` — `Index`, `Create`, `Edit`, `Save`, `Open`, `Close`, `Send` GET/POST, `Results`, `ExportCsv`, `ExportJson` all inherit) |
| `SurveysApiController` | Class | `[ServiceFilter(typeof(SurveyApiKeyAuthFilter))]` (API-key auth) | `SurveyApiKeyAuthFilter` (key-authed agent read API at `/api/surveys` — `List`, `Definition`, `Responses`, `Aggregates`) |

### Calendar Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CalendarController` | Class | `[Authorize]` (authenticated) | — |
| `ICalFeedApiController` | Action | `AllowAnonymous` | — (personal iCal feed at `/api/ical/{userId:guid}/{token:guid}.ics`; secret is the user's stored `ICalToken`; all failure modes return 404 — no oracle distinguishing unknown user from wrong token) |

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
| `AccountController.GateLogin` (GET/POST) | Action | (no `[Authorize]`) | — (shared kiosk credential login at `/Account/GateLogin`; IP-throttled via `GateLoginThrottle`; never gated by role — the gate-terminal account holds no roles) |
| `UserController` | Class | `[Authorize]` (authenticated) | — (account-status wall + cancel-deletion landings at `/User`; exempt from `MembershipRequiredFilter` since these ARE the redirect targets — each action self-checks the caller's `UserState`; #881) |
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
| `DevSeedController.ResetDashboard` | Action | `Admin` | `PolicyNames.AdminOnly` |

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
3. **`User.IsInRole(RoleNames.X)` direct calls** — no longer present in any view file (all build-hash, Events-dropdown, Guide-layout, and UsersAdmin/AdminDetail call sites have been migrated to `AuthService.AuthorizeAsync` flag variables or `authorize-policy` attributes — verified 2026-05-28).
4. **`Model.CanX` / `Model.IsX` view-model properties** — for resource-relative checks (coordinator-of-this-team, lead-of-this-camp, can-edit-this-budget) and for status-driven UI (suspended badge, approved badge, etc.). The view does not know about roles; the controller / view-model author resolved authorization upstream.

`RoleChecks.*` and `ShiftRoleChecks.*` are no longer invoked from any view file (Phase 1 retirement complete — verified 2026-05-28).

### Nav Layout (`_Layout.cshtml`)

| Line | Check | Controls |
|---|---|---|
| 37 | `var isEventsAdminOrAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.EventsAdminOrAdmin)).Succeeded` | Drives `isEventsAdminOrAdmin` flag for the Events admin sub-dropdowns below |
| 38 | `var isFullAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Drives `isFullAdmin` flag for build-hash tooltip on brand link (commit SHA on hover) — gated to FullAdmin (`AdminOnly`), not `AnyAdminRole` |
| 97 | `authorize-policy="AppAccess"` | City Planning nav link |
| 102 | `authorize-policy="AppAccess"` | Events dropdown (feature-flagged) |
| 108 | `if (isEventsAdminOrAdmin)` | Guide Dashboard / Moderate / Export dropdown items |
| 115 | `if (isEventsAdminOrAdmin)` | Guide Settings / Categories / Venues dropdown items |
| 131 | `authorize-policy="AppAccess"` | Shifts nav link (no separate shift access — merged into `AppAccess`) |
| 134 | `authorize-policy="AppAccess"` | Budget nav link |
| 137 | `authorize-policy="AnyAdminRole"` | Admin nav link (entry to admin shell) |

### Login Partial (`_LoginPartial.cshtml`)

| Line | Check | Controls |
|---|---|---|
| 50 | `authorize-policy="AppAccess"` | Governance link in profile dropdown |

### Guide Layout (`_GuideLayout.cshtml`)

| Line | Check | Controls |
|---|---|---|
| 40 | `authorize-policy="AdminOnly"` | "Refresh from GitHub" button |

### Shift Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Shifts/Index.cshtml` | 67 | `authorize-policy="ShiftDepartmentManager"` | Dashboard button |
| `Shifts/Index.cshtml` | 68 | `authorize-policy="AdminOnly"` | Settings button |
| `Shifts/NoActiveEvent.cshtml` | 8 | `authorize-policy="AdminOnly"` | "Configure Event Settings" link |
| `ShiftDashboard/Index.cshtml` | 83 | `authorize-policy="ShiftDashboardAccess"` | Voluntell card |
| `ShiftDashboard/Index.cshtml` | 220 | `authorize-policy="ShiftDashboardAccess"` | Volunteer search column |
| `ShiftDashboard/Index.cshtml` | 304, 314 | `authorize-policy="ShiftDashboardAccess"` | Per-row signup-action cells |
| `VolunteerTracking/Index.cshtml` | 8 | `(await AuthService.AuthorizeAsync(User, PolicyNames.VolunteerTrackingWrite)).Succeeded` | Drives `canWrite` flag for write controls below |
| `VolunteerTracking/_VolunteerHeatmap.cshtml` | 9 | `(await AuthService.AuthorizeAsync(User, PolicyNames.VolunteerTrackingWrite)).Succeeded` | Drives `canWrite` flag for cell-level write actions |
| `VolunteerTracking/_VolunteerUnbookedHeatmap.cshtml` | 8 | `(await AuthService.AuthorizeAsync(User, PolicyNames.VolunteerTrackingWrite)).Succeeded` | Drives `canWrite` flag for cell-level write actions |

### Profile Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Profile/Index.cshtml` | 15 | `authorize-policy="HumanAdminBoardOrAdmin"` | "Admin" link to AdminDetail |
| `Profile/Index.cshtml` | 70 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminBoardOrAdmin)).Succeeded` | `ProfileCardViewMode.Admin` vs `Public` for non-own profiles |
| `Profile/Emails.cshtml` | 17 | `(await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Admin-only email management controls |
| `UsersAdmin/AdminDetail.cshtml` | 10 | `var isAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Drives `isAdmin` flag for Admin-only data blocks |

### Board / Onboarding Review Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Governance/BoardVoting/Detail.cshtml` | 116 | `authorize-policy="BoardOnly"` | Vote casting card |

### Team Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Team/Index.cshtml` | 20 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminBoardOrAdmin)).Succeeded` | "Summary" + "Sync Status" toolbar buttons on the Teams landing page |
| `Team/Summary.cshtml` | 22 | `authorize-policy="BoardOrAdmin"` | "Create Team" button |
| `Team/Summary.cshtml` | 50 | `authorize-policy="BoardOrAdmin"` | Actions column header |
| `Team/_AdminTeamRow.cshtml` | 44 | `(await AuthService.AuthorizeAsync(User, PolicyNames.BoardOrAdmin)).Succeeded` | Pending-shift-signup badge link |
| `Team/_AdminTeamRow.cshtml` | 96 | `authorize-policy="BoardOrAdmin"` | Actions column cell (Edit/Deactivate buttons) |
| `Team/EditTeam.cshtml` | 81 | `authorize-policy="AdminOnly"` | "Sensitive team" checkbox |

### Camp Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Camp/Index.cshtml` | 11 | `authorize-policy="CampAdminOrAdmin"` | "Camp Admin" link |
| `CampAdmin/Index.cshtml` | 471 | `authorize-policy="AdminOnly"` | Danger Zone card (Delete Camp) |

### Ticket Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Ticket/Index.cshtml` | 279 | `authorize-policy="TicketAdminOrAdmin"` | "Sync Now" form |
| `Ticket/Index.cshtml` | 285 | `authorize-policy="AdminOnly"` | "Full Re-sync" form |
| `Ticket/Index.cshtml` | 293 | `authorize-policy="TicketAdminOrAdmin"` | Export link |
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

### Admin / Store Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Admin/Index.cshtml` | 124 | `authorize-policy="AdminOnly"` | Recent-activity / dashboard split-panels on the admin landing page |
| `Store/Index.cshtml` | 10 | `authorize-policy="StoreCatalogAdmin"` | Catalog / Summary / Payments admin button group on the Store landing page |

### Shared Components

| View | Line | Check | Controls |
|---|---|---|---|
| `Shared/Components/ProfileCard/Default.cshtml` | 29 | `(await AuthService.AuthorizeAsync(User, PolicyNames.HumanAdminBoardOrAdmin)).Succeeded` | Admin / Board view of profile card |
| `Shared/_HumanPopover.cshtml` | 7 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminBoardOrAdmin)).Succeeded` | Drives `canSeeHiddenTeams` flag (hidden-team list in popover) |
| `Shared/_HumanPopover.cshtml` | 11 | `(await AuthService.AuthorizeAsync(User, PolicyNames.AnyAdminRole)).Succeeded` | Drives `canSeeCamp` flag (camp membership in popover) |
| `Shared/_HumanPopover.cshtml` | 19 | `(await AuthService.AuthorizeAsync(User, PolicyNames.HumanAdminBoardOrAdmin)).Succeeded` | HumanAdmin/Board/Admin popover details (preferred-language flag) |
| `WidgetGallery/Index.cshtml` | 1173 / 1178 | `authorize-policy="@PolicyNames.AdminOnly"` / `authorize-policy="DefinitelyNotARealPolicyName"` | Documentation/demo of the TagHelper (not production gating) |
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
| Scanner access (roles + gate terminal) | `[Authorize(Policy = PolicyNames.ScannerAccess)]` | (no current view spelling) |
| CampAdmin or Admin | `[Authorize(Policy = PolicyNames.CampAdminOrAdmin)]` | `authorize-policy="CampAdminOrAdmin"` |
| HumanAdmin/Board/Admin | `[Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]` | `authorize-policy="HumanAdminBoardOrAdmin"` |
| FeedbackAdmin or Admin | `[Authorize(Policy = PolicyNames.FeedbackAdminOrAdmin)]` | `authorize-policy="FeedbackAdminOrAdmin"` |
| FinanceAdmin or Admin | `[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]` | `authorize-policy="FinanceAdminOrAdmin"` |
| CantinaAdmin or Admin | `[Authorize(Policy = PolicyNames.CantinaAdminOrAdmin)]` | `authorize-policy="CantinaAdminOrAdmin"` |
| Store catalog admin | `[Authorize(Policy = PolicyNames.StoreCatalogAdmin)]` | `authorize-policy="StoreCatalogAdmin"` (Store landing-page admin button group) |
| EventsAdmin or Admin | `[Authorize(Policy = PolicyNames.EventsAdminOrAdmin)]` | `(await AuthService.AuthorizeAsync(User, PolicyNames.EventsAdminOrAdmin)).Succeeded` |
| Review queue access | `[Authorize(Policy = PolicyNames.ReviewQueueAccess)]` | (no current view spelling) |
| Consent coordinator + B/A | `[Authorize(Policy = PolicyNames.ConsentCoordinatorBoardOrAdmin)]` | (no current view spelling) |
| Board only | `[Authorize(Policy = PolicyNames.BoardOnly)]` | `authorize-policy="BoardOnly"` |
| Shift dashboard access | `[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]` | `authorize-policy="ShiftDashboardAccess"` |
| Shift department manager | `[Authorize(Policy = PolicyNames.ShiftDepartmentManager)]` | `authorize-policy="ShiftDepartmentManager"` |
| Volunteer tracking write | `[Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]` | `(await AuthService.AuthorizeAsync(User, PolicyNames.VolunteerTrackingWrite)).Succeeded` |
| App access (Active or any role) | `[Authorize(Policy = PolicyNames.AppAccess)]` | `authorize-policy="AppAccess"` |
| Resource: team coord/admin | `_authorizationService.AuthorizeAsync(User, team, TeamOperationRequirement.{ManageCoordinators, ManageEarlyEntry})` | `Model.IsCurrentUserCoordinator` / `Model.CanManageEarlyEntry` (view-model) |
| Resource: camp lead/admin | `_authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` | `Model.IsCurrentUserLead \|\| Model.IsCurrentUserCampAdmin` (view-model) |
| Resource: camp-event submit | `_authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.SubmitEvent)` | (no view spelling — controller-only) |
| Resource: budget edit | `_authorizationService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)` | `Model.CanEdit` (view-model) |
| Resource: container place/manage | `_authorizationService.AuthorizeAsync(User, target, ContainerOperationRequirement.{Manage, Place})` | `Model.CanX` (view-model) |
| Resource: store order | `authService.AuthorizeAsync(User, order, StoreOrderOperationRequirement.{View, Create, AddLine, RemoveLine, EditCounterparty, Pay, Delete})` (and `StoreOrderCreateContext` for Create) | `Model.CanManageByCounterparty` / per-order flags (view-model) |
| Resource: expense report | `authService.AuthorizeAsync(User, report, new ExpenseReportOperationRequirement(ExpenseReportOperation.X))` — `View`, `Endorse`, `CoordinatorReject`, `Approve`, `FinanceReject` (submitter-side actions use inline `SubmitterUserId` owner checks instead) | `Model.CanX` (view-model) |
| Resource: IBAN access | `IbanAccessHandler` / `IbanAccessRequirement` are **registered but have no production call site** (only `IbanAccessHandlerTests` exercise them). `UsersAdminController.RevealIban` is gated by `[Authorize(Policy = PolicyNames.AdminOnly)]`; expense-report IBAN views show masked self-IBAN with no resource check. | (none today) |
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
| `UsersAdminController.AdminOutbox` | `HumanAdminBoardOrAdmin` | No visible button in `AdminList` view (accessed via URL pattern). |

### Runtime-Only Guards (no attribute, enforced in method body)

These actions rely on `if` checks + early return/forbid instead of `[Authorize(Policy)]`:

| Controller | Action | Guard |
|---|---|---|
| `ShiftAdminController` | All non-public actions | Coordinator-of-department check via `ResolveDepartmentManagementAsync` → `HumansTeamControllerBase.ResolveDepartmentAccessAsync` (resource-based) |
| `TeamAdminController` | Most non-public actions | Coordinator-of-team check via `HumansTeamControllerBase.ResolveTeamManagementAsync` (`TeamOperationRequirement.ManageCoordinators`); `RoleChecks.IsTeamsAdmin(User)` / `RoleChecks.IsAdmin(User)` toggle management features |
| `TeamAdminController` | `EarlyEntry` / `AddEarlyEntry` / `EditEarlyEntry` / `RemoveEarlyEntry` | EE-management check via `ResolveEarlyEntryManagementAsync` → `_authorizationService.AuthorizeAsync(User, team, TeamOperationRequirement.ManageEarlyEntry)` (Admin/TeamsAdmin/Board any team; `EETeamAdmin` any team; coordinator own team) |
| `BudgetController` | `Index`, `Summary`, `CategoryDetail`, line-item CRUD | `_authService.AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` and `_authService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)` |
| `CampController` | All management actions | `_authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` via `HumansCampControllerBase` |
| `ContainerController` | All non-public actions | `_authorizationService.AuthorizeAsync(User, target, ContainerOperationRequirement.{Manage, Place})` (resource-based) |
| `EventsController` | Barrio-event submit/create/edit/update/withdraw | `_authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.SubmitEvent)` via `HumansCampControllerBase.ResolveCampEventManagementAsync` (resource-based); plus owner-or-`RoleChecks.IsEventsAdmin` gate on Edit/Update endpoints |
| `ExpensesController` | Detail/Attachment view, Endorse, CoordinatorReject, Approve, FinanceReject | `authService.AuthorizeAsync(User, report, new ExpenseReportOperationRequirement(ExpenseReportOperation.X))` (resource-based) |
| `ExpensesController` | Submitter-side actions (Edit, line CRUD incl. AddMileage/AddPerDiem, Submit, Withdraw, Iban) | Inline owner check `report.SubmitterUserId != user.Id → Forbid()` (#900) |
| `TeamController` | `EditTeam` (POST) `IsSensitive` flag | `authorizationService.AuthorizeAsync(User, PolicyNames.AdminOnly)` — non-Admin posts leave `IsSensitive` unchanged |
| `StoreController` | Order CRUD/pay | `_authService.AuthorizeAsync(User, order, StoreOrderOperationRequirement.*)` (resource-based) |
| `IssuesController` | All mutating actions | `_authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)` (resource-based) |
| `CityPlanningController` / `CityPlanningApiController` | All actions except `Index`/`GetState` | `RoleChecks.IsCampAdmin(User)` and lead-of-camp checks; three API endpoints also call `_authorizationService.AuthorizeAsync` |
| `FeedbackController` | `Index`, `Detail`, `PostMessage` | `RoleChecks.IsFeedbackAdmin(User)` to determine admin vs user view |
| `UsersAdminController.AddRole/EndRole` | After `[Authorize(Policy)]` attribute | `_authorizationService.AuthorizeAsync(User, roleName, RoleAssignmentOperationRequirement.Manage)` enforces the role-list filter |
| `ProfileController` email-edit endpoints (18 actions) | After class-level `[Authorize]` | `_authorizationService.AuthorizeAsync(User, userId, UserEmailOperations.Edit)` (resource-based) |
| `TicketController.Index` | After class-level policy | `RoleChecks.CanAccessFinance(User)` toggles finance-only metrics |
| `MembershipRequiredFilter` | All authenticated requests | Gates the app purely on stored `UserState` (stamped on the principal by `RoleAssignmentClaimsTransformation`): only `Active` reaches the app; `DeletePending` → `/User/Deletion`, Suspended/AdminSuspended/Rejected/Deleted/Merged → `/User/Status`, Bare/unseeded → `/OnboardingWidget`. Roles do not bypass the gate. Exempt controllers (`Account`, `OnboardingWidget`, `Profile`, `Consent`, `User`, `Language`, `Guest`, `GovernanceApplications`, `Feedback`, `Notifications`) and `[AllowAnonymous]` pass through. Replaced the deleted `IsActiveMember` / `ActiveMemberOrShiftAccess` requirement+handler pairs and `RoleChecks.BypassesMembershipRequirement` (#881). |
| `NameRequiredFilter` | All requests | Global action filter (registered in `Program.cs` before `MembershipRequiredFilter`). Redirects any authenticated user with no real `BurnerName` to the name form; never blocks sign-in (only redirects). Exempt controllers (`Account`, `Language`), exempt actions (`OnboardingWidget/Names`, `Home/Error`, `Home/Privacy`), and `[AllowAnonymous]` pass through. |
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
| `CampComplianceAccess` | CampAdmin, Admin OR any team/sub-team coordinator | `PolicyNames.CampComplianceAccess` (composite — `CampComplianceAccessHandler`) |
| `TicketAdminBoardOrAdmin` | TicketAdmin, Admin, Board | `PolicyNames.TicketAdminBoardOrAdmin`, `RoleChecks.CanAccessTickets` |
| `TicketAdminOrAdmin` | TicketAdmin, Admin | `PolicyNames.TicketAdminOrAdmin`, `RoleChecks.CanManageTickets` |
| `ScannerAccess` | TicketAdmin, Admin, Board OR `SystemUserIds.GateTerminal` (by NameIdentifier claim) | `PolicyNames.ScannerAccess` (composite assertion — gate-terminal account admitted by id, not by role) |
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
| `AppAccess` | `UserState == Active` | `PolicyNames.AppAccess` (single `RequireAssertion` — the nav-visibility gate; replaced the former `IsActiveMember` / `ActiveMemberOrShiftAccess` split) |
| `HumanAdminOnly` | HumanAdmin AND NOT (Admin OR Board) | `PolicyNames.HumanAdminOnly` (composite — `HumanAdminOnlyHandler`) |
| `MedicalDataViewer` | Admin, NoInfoAdmin | `PolicyNames.MedicalDataViewer`, `ShiftRoleChecks.CanViewMedical` |
| `AgentRateLimit` | (per-user rate-limit) | `PolicyNames.AgentRateLimit` (resource-based — `AgentRateLimitHandler`) |

### Notes on Policy Design

- `ShiftDashboardAccess` and `ShiftDepartmentManager` are intentionally distinct: dashboard access is role-list-based, department manager additionally permits any team manager/coordinator (composite via `IsAnyTeamManagerOrCoordinatorHandler`).
- `AppAccess` is the single nav-visibility gate: `UserState == Active` (the user entered their legal name). A plain `RequireAssertion` — no custom requirement/handler. It replaced the former `IsActiveMember` / `ActiveMemberOrShiftAccess` policies (and there is no separate shift access).
- `CampComplianceAccess` is deliberately broader than `CampAdminOrAdmin`: it short-circuits for CampAdmin/Admin and otherwise admits any team/sub-team coordinator (composite via `CampComplianceAccessHandler`, reusing the same `IShiftManagementService.GetCoordinatorTeamIdsAsync` lookup as `IsAnyTeamManagerOrCoordinatorHandler`). It gates only the read-only Barrios compliance matrix; the camp-management surface in `CampAdminController` stays CampAdmin-only.
- `HumanAdminOnly` is a composite policy used for the nav "Humans" link that only shows when the user has HumanAdmin but not the broader Board/Admin access.
- `MedicalDataViewer` is a data-access policy, not a page-access policy. It controls whether medical fields are visible within pages the user already has access to.
- `AnyAdminRole` gates the admin-shell entry point (`/Admin`). Sidebar items inside the shell are filtered per-item by `AdminSidebarViewComponent` against each item's policy. The role list mirrors the top-nav check in `_Layout.cshtml` and includes the grantable `CantinaAdmin` role added with the Cantina coordinator surface (feature #36).
- Object-relative policies (coordinator of specific team, camp lead of specific camp, camp-event submitter, budget category for coordinator's department, manageable role for HumanAdmin, expense reports, store orders, containers, issues, user-email edits, agent rate-limit) are implemented as resource-based authorization handlers — see §6.

---

## 6. Resource-Based Authorization Handlers

Resource-based authorization handlers are subclasses of `AuthorizationHandler<TRequirement, TResource>` (or `AuthorizationHandler<TRequirement>` / `IAuthorizationHandler` directly when the same handler covers multiple resource shapes) that evaluate whether a user can perform an operation on a specific resource instance. They are invoked via `IAuthorizationService.AuthorizeAsync(User, resource, requirement)` from controllers (or controller base classes).

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `TeamAuthorizationHandler` | `TeamOperationRequirement` (`ManageCoordinators`, `ManageEarlyEntry`) | `TeamInfo` | `src/Humans.Web/Authorization/Requirements/TeamAuthorizationHandler.cs` — Admin/TeamsAdmin/Board: any team, any op; `EETeamAdmin`: any team for `ManageEarlyEntry` only; team coordinator: own team only (both ops) |
| `CampAuthorizationHandler` | `CampOperationRequirement` (`Manage`, `SubmitEvent`) | `CampLookup` / `Camp` entity / camp id (`Guid`) | `src/Humans.Web/Authorization/Requirements/CampAuthorizationHandler.cs` |
| `BudgetAuthorizationHandler` | `BudgetOperationRequirement` (`Edit`) | `BudgetCategorySnapshot` | `src/Humans.Web/Authorization/Requirements/BudgetAuthorizationHandler.cs` |
| `ContainerAuthorizationHandler` | `ContainerOperationRequirement` (`Manage`, `Place`) | `ContainerAuthorizationTarget` | `src/Humans.Web/Authorization/Requirements/ContainerAuthorizationHandler.cs` |
| `StoreOrderAuthorizationHandler` | `StoreOrderOperationRequirement` (`View`, `Create`, `AddLine`, `RemoveLine`, `EditCounterparty`, `Pay`, `Delete`) | `OrderDto` / `StoreOrderCreateContext` | `src/Humans.Web/Authorization/Requirements/StoreOrderAuthorizationHandler.cs` |
| `ExpenseReportAuthorizationHandler` | `ExpenseReportOperationRequirement` (`View`, `Edit`, `Submit`, `Withdraw`, `Endorse`, `CoordinatorReject`, `Approve`, `FinanceReject`, `CategoryOverride`) | `ExpenseReportDto` | `src/Humans.Web/Authorization/Requirements/ExpenseReportAuthorizationHandler.cs` |
| `IbanAccessHandler` | `IbanAccessRequirement` | (intrinsic — `TargetUserId` / `ReportId` / `IsAdminPageContext` fields on requirement) | `src/Humans.Web/Authorization/Requirements/IbanAccessHandler.cs` — **registered in DI but no production call site today** (only `IbanAccessHandlerTests`); `UsersAdminController.RevealIban` uses `[Authorize(Policy = AdminOnly)]` instead. |
| `IssuesAuthorizationHandler` | `IssuesOperationRequirement` (`Handle`) | `IssueDetail` | `src/Humans.Web/Authorization/Requirements/IssuesAuthorizationHandler.cs` |
| `UserEmailAuthorizationHandler` | `UserEmailOperationRequirement` (`Edit`) | `Guid` (target user id) | `src/Humans.Web/Authorization/Requirements/UserEmailAuthorizationHandler.cs` |
| `RoleAssignmentAuthorizationHandler` | `RoleAssignmentOperationRequirement` (`Manage`) | `string` (roleName) | `src/Humans.Application/Authorization/RoleAssignmentAuthorizationHandler.cs` |
| `AgentRateLimitHandler` | `AgentRateLimitRequirement` | `Guid` (user id) | `src/Humans.Web/Authorization/Handlers/AgentRateLimitHandler.cs` |

Composite (non-resource) handlers registered alongside the above:

| Handler | Requirement | Path |
|---|---|---|
| `HumanAdminOnlyHandler` | `HumanAdminOnlyRequirement` | `src/Humans.Web/Authorization/Requirements/HumanAdminOnlyHandler.cs` |
| `IsAnyTeamManagerOrCoordinatorHandler` | `IsAnyTeamManagerOrCoordinatorRequirement` | `src/Humans.Web/Authorization/Requirements/IsAnyTeamManagerOrCoordinatorHandler.cs` |
| `CampComplianceAccessHandler` | `CampComplianceAccessRequirement` | `src/Humans.Web/Authorization/Requirements/CampComplianceAccessHandler.cs` (short-circuits for CampAdmin/Admin; else admits any team/sub-team coordinator via `IShiftManagementService.GetCoordinatorTeamIdsAsync`) |

### `IAuthorizationService.AuthorizeAsync` Call Sites

| File | Line | Call |
|---|---|---|
| `src/Humans.Web/Controllers/HumansTeamControllerBase.cs` | 23 | `AuthorizeAsync(User, team, TeamOperationRequirement.ManageCoordinators)` (`ResolveTeamManagementAsync`) |
| `src/Humans.Web/Controllers/HumansTeamControllerBase.cs` | 36 | `AuthorizeAsync(User, team, TeamOperationRequirement.ManageEarlyEntry)` (`ResolveEarlyEntryManagementAsync`) |
| `src/Humans.Web/Controllers/TeamController.cs` | 169 | `AuthorizeAsync(User, teamInfo, TeamOperationRequirement.ManageEarlyEntry)` (drives `CanManageEarlyEntry` view-model flag on team details) |
| `src/Humans.Web/Controllers/TeamController.cs` | 734 | `AuthorizeAsync(User, PolicyNames.AdminOnly)` (EditTeam POST — `IsSensitive` leave-unchanged guard for non-Admin editors) |
| `src/Humans.Web/Controllers/HumansCampControllerBase.cs` | 24 | `AuthorizeAsync(User, campId, CampOperationRequirement.Manage)` |
| `src/Humans.Web/Controllers/HumansCampControllerBase.cs` | 58 | `AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` |
| `src/Humans.Web/Controllers/HumansCampControllerBase.cs` | 88 | `AuthorizeAsync(User, camp, CampOperationRequirement.SubmitEvent)` |
| `src/Humans.Web/Controllers/BudgetController.cs` | 29 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` |
| `src/Humans.Web/Controllers/BudgetController.cs` | 92 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` |
| `src/Humans.Web/Controllers/BudgetController.cs` | 112 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` |
| `src/Humans.Web/Controllers/BudgetController.cs` | 118 | `AuthorizeAsync(User, detail.Category, BudgetOperationRequirement.Edit)` |
| `src/Humans.Web/Controllers/BudgetController.cs` | 229 | `AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)` |
| `src/Humans.Web/Controllers/ContainerController.cs` | 24 | `AuthorizeAsync(User, target, requirement)` (private helper) |
| `src/Humans.Web/Controllers/ExpensesController.cs` | 206, 589, 635, 655, 700, 721 | `AuthorizeAsync(User, report, new ExpenseReportOperationRequirement(ExpenseReportOperation.X))` — `View` (Detail 206, Attachment 589), `Endorse` 635, `CoordinatorReject` 655, `Approve` 700, `FinanceReject` 721 |
| `src/Humans.Web/Controllers/StoreController.cs` | 52, 74, 77, 78, 79, 101, 118, 136, 170, 188, 217, 247, 274, 299 | `AuthorizeAsync(User, order/resource, StoreOrderOperationRequirement.X)` (and `StoreOrderCreateContext` / `StoreOrderLineContext` for Create at 101 and line-deadline-aware AddLine/RemoveLine at 247/274) |
| `src/Humans.Web/Controllers/IssuesController.cs` | 190, 260, 306, 333, 360, 385 | `AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)` |
| `src/Humans.Web/Controllers/CityPlanningApiController.cs` | 274, 299, 337 | `AuthorizeAsync(User, ...)` (resource-based) |
| `src/Humans.Web/Controllers/ProfileController.cs` | 658, 691, 736, 778, 815, 852, 889, 909, 953, 1032, 1048, 1074, 1107, 1133, 1159, 1335, 1361, 1405 | `AuthorizeAsync(User, userId, UserEmailOperations.Edit)` (18 email-edit endpoints) |
| `src/Humans.Web/Controllers/ProfileController.cs` | 1815 | `AuthorizeAsync(User, PolicyNames.TicketAdminBoardOrAdmin)` (onsite-chip visibility gate) |
| `src/Humans.Web/Controllers/ProfileController.cs` | 1911 | `AuthorizeAsync(User, PolicyNames.PrivilegedSignupApprover)` (inside `BuildSentMessagesContextAsync` private helper — gates whether a non-own-profile viewer sees the "sent messages" panel on the profile page; admits coordinators or `PrivilegedSignupApprover` role) |
| `src/Humans.Web/Controllers/UsersAdminController.cs` | 301 | `AuthorizeAsync(User, model.RoleName, RoleAssignmentOperationRequirement.Manage)` (AddRole — moved off `ProfileController` in #901) |
| `src/Humans.Web/Controllers/UsersAdminController.cs` | 339 | `AuthorizeAsync(User, roleAssignment.RoleName, RoleAssignmentOperationRequirement.Manage)` (EndRole — moved off `ProfileController` in #901) |
| `src/Humans.Web/Controllers/AgentController.cs` | 48 | `AuthorizeAsync(User, user.Id, PolicyNames.AgentRateLimit)` |
| `src/Humans.Web/TagHelpers/AuthorizeViewTagHelper.cs` | 54 | `AuthorizeAsync(user, Policy)` (driver of `<authorize-policy>` view tags) |
| `src/Humans.Web/ViewComponents/AdminSidebarViewComponent.cs` | 31 | `AuthorizeAsync(HttpContext.User, null, item.Policy)` (filters admin sidebar) |

---

## 7. Notes / Known Deviations

- **No `[Authorize(Roles = ...)]` attributes remain anywhere in `src/`** — every controller/action `[Authorize]` attribute now references a `PolicyNames` constant or is a bare authenticated/`[AllowAnonymous]` marker (verified 2026-05-29). `DevSeedController.ResetDashboard`, formerly the last `[Authorize(Roles = RoleNames.Admin)]` holdout, now uses `[Authorize(Policy = PolicyNames.AdminOnly)]`.
- **`ScannerController` uses `PolicyNames.ScannerAccess`**, not `TicketAdminBoardOrAdmin` — the `ScannerAccess` policy is a composite assertion that additionally admits the shared gate-terminal account by its well-known `SystemUserIds.GateTerminal` NameIdentifier claim so the kiosk session can scan without holding any role (added as part of #930 gate-terminal login).
- **`SurveyController` is `[AllowAnonymous]`** — the entire public survey wizard is unauthenticated; identity flows from the invitation token, not the principal. `SurveyAdminController` (`BoardOrAdmin`) and `SurveysApiController` (`SurveyApiKeyAuthFilter`) are the gated surfaces.
- **`ICalFeedApiController` is `[AllowAnonymous]`** — the personal iCal feed uses a secret token in the URL for authentication; all failure modes return 404 to prevent oracle attacks.
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
| `EETeamAdmin` | `"EETeamAdmin"` |

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
| `GetAssignableRoles` / `CanManageRole` | `RoleAssignmentOperationRequirement.Manage` (resource-based, see §6) |

### ShiftRoleChecks Methods → Canonical Policy Mapping

| Method | Canonical Policy |
|---|---|
| `IsPrivilegedSignupApprover` | `PrivilegedSignupApprover` |
| `CanManageDepartment` | `ShiftDepartmentManager` (role-list portion; composite extends with team-manager OR) |
| `CanAccessDashboard` | `ShiftDashboardAccess` |
| `CanViewMedical` | `MedicalDataViewer` |
