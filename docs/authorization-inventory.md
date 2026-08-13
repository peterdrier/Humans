# Authorization Inventory

Originally produced as Phase 0 of the first-class authorization transition plan (historical; the plan doc has since been pruned). **Phase 1 is complete:** every canonical policy in §5 is registered in `AuthorizationPolicyExtensions.AddHumansAuthorizationPolicies`, all controllers (including the Events Guide section, which now uses `[Authorize(Policy = PolicyNames.EventsAdminOrAdmin)]`) use `[Authorize(Policy = PolicyNames.X)]`, the `authorize-policy` TagHelper resolves through `IAuthorizationService`, and views no longer call `RoleChecks.*` / `ShiftRoleChecks.*` directly. **Phase 2 (resource-based authorization)** has shipped multiple vertical slices — see §6 (`TeamAuthorizationHandler`, `CampAuthorizationHandler`, `BudgetAuthorizationHandler`, `RoleAssignmentAuthorizationHandler`, `ContainerAuthorizationHandler`, `ExpenseReportAuthorizationHandler`, `IbanAccessHandler`, `OrderAuthorizationHandler`, `UserEmailAuthorizationHandler`, `IssuesAuthorizationHandler`, `AgentRateLimitHandler`). **Phase 3 (service-layer enforcement) is cancelled.**

Generated 2026-04-03. Refreshed 2026-08-12 (via `/freshness-sweep`, full re-scan against `04d2490f0..fa8b36737`: the G5 overnight batch (`af9242150`, #1263) relocated `GateController`/`GateVendorBackfillAdminController` (→ `src/Sections/Humans.Gate`), `BudgetController`/`BudgetAdminController` (→ `src/Sections/Humans.Budget`), `CalendarController` (→ `Humans.Calendar`), `CampaignController` (→ `Humans.Campaigns`), `CityPlanningController`/`CityPlanningApiController` (→ `Humans.CityPlanning`), `FeedbackController`/`FeedbackApiController` (→ `Humans.Feedback`), `GovernanceController`/`GovernanceApplicationsController`/`GovernanceBoardVotingController` (→ `Humans.Governance`), `IssuesController`/`IssuesApiController` (→ `Humans.Issues`), `NotificationsController` (→ `Humans.Notifications`), and `ScannerController` (→ `Humans.Scanner`); `8018be9d3` (#1259) moved `AgentController`/`AgentApiController`/`AdminAgentController` to `Humans.Agent`; `cde511e4b` (#1251) moved `SurveyController`/`SurveyAdminController`/`SurveysApiController` to `Humans.Surveys` — all namespace-only moves with no role/policy/attribute changes (verified line-by-line for every relocated `[Authorize]`/`AuthorizeAsync` call site), except that the added `using` imports each move forced shifted line numbers in `BudgetController.cs` (+1), `IssuesController.cs` (+2), and `CityPlanningApiController.cs` (-1) — corrected in §6's call-site table. `4f65c7617` (#1261, "Holded API v2 + Humans.Holded section") shipped a **new** `HoldedController` at `/Holded` (class-level `FinanceAdminOrAdmin`, same policy as Finance's own controller) — added to §1 as a new Holded Section; `f5e4b8bdc` (#1267, chart-of-accounts split) touched `FinanceController.Creditors` (added `sort`/`dir` query params, same route/policy) with no authorization-surface change; `f5e4b8bdc` together with `4f65c7617` also **removed** `FinanceController.ResyncCreditorLedger` (`POST /Finance/Creditors/Resync`) — its full-history-resync job is now `HoldedController.FullSync`. `250fda95e` (#1265, "retire mileage and per-diem line creation") removed `ExpensesController.AddMileage`/`AddPerDiem` and updated this doc directly in the same commit — already reflected; its ~44-line deletion shifted the file's later `AuthorizeAsync` call sites (Attachment/Endorse/CoordinatorReject/Approve/FinanceReject), corrected in §6. `8018be9d3`'s Agent move is also where `AgentController.Ask`'s rate-limit check changed from `_auth.AuthorizeAsync(User, user.Id, PolicyNames.AgentRateLimit)` to `auth.AuthorizeAsync(User, user.Id, [new AgentRateLimitRequirement()])` — the `PolicyNames.AgentRateLimit` constant and its `AddPolicy` registration were deleted; `AgentRateLimitHandler`/`AgentRateLimitRequirement` are now internal to `Humans.Agent` and the requirement is instantiated directly at its one call site (design §15 step 6) rather than resolved via a named policy string — §1, §3 ("Resource: agent rate-limit"), §4, §5 (row removed — no longer a registered named policy), and §6 updated accordingly. `bc0a32315` (team_role_definitions realignment) and `65fdae7de`/`515267af3`/`d08015ad9`/`e389bb204`/`f6c318e53`/dependency-bump commits in the same range touched no authorization surface (verified). All other §1/§2/§3/§4/§5/§7/Appendix entries re-verified unchanged). Previously refreshed 2026-08-10 (via `/freshness-sweep`, full re-scan against `80f14b801..04d2490f0`: four G5 section-extraction PRs (nobodies-collective/Humans#866) relocated controllers and resource-based authorization handlers with no role/policy assignment changes — `6bfa9092b` (#1223) moved Store to `src/Sections/Humans.Store` and, having dropped the section's now-redundant `Store` prefix internally, renamed `StoreOrderAuthorizationHandler`→`OrderAuthorizationHandler`, `StoreOrderOperationRequirement`→`OrderOperationRequirement`, `StoreOrderCreateContext`→`OrderCreateContext` (also moved `HumansControllerBase`/`RoleChecks` to `Humans.UI`, already reflected by the prior sweep's UI-extraction note); `25c72f8be` (#1235) moved the Events Guide controllers to `src/Sections/Humans.Events` and `HumansCampControllerBase`/`ApiControllerBase`/`CampOperationRequirement` to `Humans.UI` (same policies, same attributes — pure relocation, verified by diff); `928ab7391` (#1239) moved Containers to `src/Sections/Humans.Containers` (handler unchanged) and split the old 31-action `FinanceController` in two — the 8 Holded/creditor actions became the new `src/Sections/Humans.Finance/Controllers/FinanceController.cs` (same `FinanceAdminOrAdmin` policy), the other 23 Budget-CRUD actions stayed in Shell renamed to `BudgetAdminController` on the same `[Route("Finance")]` (added to §1's Finance Section, table row split accordingly); `2e17d5539` (#1240) moved Expenses to `src/Sections/Humans.Expenses` (`ExpenseReportAuthorizationHandler`/`IbanAccessHandler` unchanged); `1d334ce77` reshaped `ExpensesController`'s internals (member-ledger fix) shifting its `AuthorizeAsync` call-site line numbers with no operation changes; all four moved sections now register their own resource-based handlers via each section's `Section.cs` DI rather than the centralized `AuthorizationPolicyExtensions` (policy *definitions* — all 29 `AddPolicy` calls — remain centralized there, unchanged); §1 (Finance/Budget rows), §3, §4, and §6 (handler table + call-site line numbers/paths) updated throughout; the `refactor(809)` BurnSettingsInfo migration commits and the `refactor(992)`/`refactor(996)` cross-section FK/EF-nav removals in the same range touched controller files but carried no `[Authorize]`/`RoleChecks`/`AuthorizeAsync` changes (verified by direct line-by-line diff, not assumed); all other §1/§2/§3/§4/§5/§7/Appendix entries re-verified unchanged). Previously refreshed 2026-08-08 (via `/freshness-sweep`, full re-scan against `980896a1d..38682230b`: `faea03e66` (#1220, "extract Humans.UI — the shared view layer out of Humans.Web") relocated `PolicyNames.cs` to `src/Humans.UI/Authorization/PolicyNames.cs` (content unchanged besides the already-noted `FeedbackAdminOrAdmin` removal), `AuthorizeViewTagHelper.cs`/`MarkdownEditorTagHelper.cs`/`NonceTagHelper.cs`/`PageHeaderTagHelper.cs` to `src/Humans.UI/TagHelpers/`, `AuditLogViewComponent`/`HumanViewComponent`/`TempDataAlertsViewComponent` to `src/Humans.UI/ViewComponents/`, and `_LoginPartial.cshtml` to `src/Humans.UI/Views/Shared/` — no role/policy assignment changed by the move, only the new `using Humans.UI.Authorization;` imports it forced, which shifted line numbers across most controllers/views by 1-6 lines (corrected throughout §2 and §6); `7b60a7308` (#1185) and the `MembershipRequiredFilter`/`RoleChecks.IsFeedbackAdmin` deletions it made were already reflected in this doc's Feedback-section and exemption-list text ahead of this sweep; `d09fcc2e1`/`fa1c9577e`/`660ba7130` reshaped `FinanceController.Creditors`/`BindCreditor` internals and added `FinanceController.UnbindCreditor` (inherits the class-level `FinanceAdminOrAdmin` policy, no new attribute) — no authorization-surface change; verified no controllers, actions, policies, or handlers were added or removed since the prior sweep beyond the above; all §1/§3/§4/§5/§7/Appendix entries re-verified unchanged). Previously refreshed 2026-08-05 (via `/freshness-sweep`, scoped re-scan against `5a9bbe198..980896a1d`: `c85abc977` dropped the `CanOpenStore` computation out of `TeamController` entirely and re-expressed it as a direct `AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminOrAdmin)` call in `Team/Details.cshtml` — the policy itself (registered by the same commit) is unchanged from the prior sweep's note and remains wired to no controller attribute, only that view boolean; `d34a8b9cc`'s HUM0031 controller decompositions (`AccountController`, `EmailController`, `EventsController`, `GovernanceApplicationsController`, `ProfileController`, `ShiftsController`, `StoreController`, `TeamAdminController`, `TeamController`, `UsersAdminDebugController`) moved logic into services/domain methods without adding, removing, or changing any `[Authorize]` attribute — verified across all ten controllers; §6 call-site line numbers corrected for `TeamController.cs` (166→160, 731→679) and `ProfileController.cs`'s 18 `UserEmailOperations.Edit` sites (658→713, 691→746, 736→791, 778→833, 815→870, 852→907, 889→944, 909→964, 953→1008, 1032→1087, 1048→1103, 1074→1129, 1107→1162, 1133→1188, 1159→1214, 1335→1390, 1361→1416, 1405→1460) plus the onsite-chip gate (1815→1872) and the `BuildSentMessagesContextAsync` `PrivilegedSignupApprover` check (1911→1968); all other entries verified unchanged). Previously refreshed 2026-08-03 (via `/freshness-sweep`, full re-scan: new **`PolicyNames.TeamsAdminOrAdmin`** policy (TeamsAdmin, Admin) registered in `AuthorizationPolicyExtensions` but not yet wired to any controller attribute — currently only referenced from `Team/Details.cshtml`'s "Open store" boolean (line 315), added to §3/§5; `TeamAdminController.Roster` (`[HttpGet("Roster")]`, action-level `[Authorize(Policy = PolicyNames.BoardOrAdmin)]` narrowing the class-level coordinator-reachable surface) was missing from §1's Teams Section table — added, along with its matching `Team/Details.cshtml` line-385 `authorize-policy="BoardOrAdmin"` Roster link in §2; §6 call-site line numbers corrected for `StoreController.cs` (52→54 … 299→301, unrelated +2 shift) and `TeamController.cs` (169→166, 734→731, unrelated -3 shift); all other entries verified unchanged). Previously refreshed 2026-07-14 (via `/freshness-sweep`, full re-scan: the **Gate section** shipped (#1066 and follow-ups #1069–#1084) — `GateController` at `/Gate` carries class-level `[Authorize(Policy = PolicyNames.ScannerAccess)]` for the read actions (`Index`, `Evaluate`, `Claim` GET, `Search`, `Leaderboard`) while the write actions (`Decision`, `Claim` POST, `ClaimPin`, `EndShift`) require the **new `PolicyNames.GateAdmit` policy** (same principals as `ScannerAccess` today — TicketAdmin/Board/Admin or the gate-terminal shared account by well-known id — but a separate composite assertion so the write path never rides the read-only Scanner gate) and the settings/PIN-management actions (`Admin` GET/POST, `SetStaffPin`, `ResetStaffPin`) require `TicketAdminOrAdmin`; gate supervisor overrides (too-early / unconfirmed-EE admit, child-without-ID waiver) are authorized by a shared override PIN (`Gate:SupervisorPin` config), server-verified with a fixed-time hash compare and brute-force-throttled via `GatePinThrottle` (fail-closed when unconfigured); staff claim PINs are verified per-user via `IGateService` with per-target-user throttle buckets; `GateVendorBackfillAdminController` (`AdminOnly`, temp page at `/Gate/Admin/VendorCheckInBackfill`) added by #1080; `ExpensesController` gained an `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` flag call at line 223 (Holded creditor binding on report detail); `VolunteerTracking/Index.cshtml` no longer performs its own `VolunteerTrackingWrite` check (the two heatmap partials retain theirs); §2/§6 line numbers updated for `_Layout.cshtml`, `Governance/BoardVoting/Detail.cshtml`, `Profile/Index.cshtml`, `UsersAdmin/AdminDetail.cshtml`, `Campaign/Detail.cshtml`, `Ticket/Index.cshtml`, `CampAdmin/Index.cshtml`, `Store/Index.cshtml`, `WidgetGallery/Index.cshtml`, `AdminSidebarViewComponent`, `BudgetController`, `ExpensesController`, and `UsersAdminController`; all other entries verified unchanged). Previously refreshed 2026-06-24 (via `/freshness-sweep`; full re-scan against worktree `freshness-sweep-2026-06-23T231018Z`: `SepaReopen` / `SepaGenerate` actions and `ExpenseReportOperation.{IncludeInSepaPayout,ReopenSepa}` are not present in this worktree's code — removed from §1, §3, §6, and the §6 call-site table; `ExpensesController` `AuthorizeAsync` call-site line numbers corrected to 206, 589, 635, 655, 700, 721; all other entries verified unchanged). Previously refreshed 2026-06-13 (via `/freshness-sweep`; `GovernanceBoardVotingController.Finalize` (POST, `AdminOnly`) added — action-level override finalizing board vote rounds; `VolunteerTrackingController.SetAvailabilityDay` / `ClearAvailabilityDay` added (`VolunteerTrackingWrite`); `ProfileController.BuildSentMessagesContextAsync` new `AuthorizeAsync(User, PolicyNames.PrivilegedSignupApprover)` call at line 1911 added to §6 call site table; line numbers in §6 call site table updated for `ExpensesController`, `ProfileController`, and `UsersAdminController` to reflect current source). Previously refreshed 2026-06-12 (via `/freshness-sweep`; catch-up: `ToggleCampFavourite` (route `/Events/Barrio/{slug}/Favourite/{eventId:guid}`) was renamed to `ToggleCardFavourite` (route `/Events/Card/Favourite/{eventId:guid}`) in #925 — whole-event favourite from the events card, now camp-and-profile-page-agnostic (missed by the 2026-06-10 sweep); #982 added `ExpensesController.SepaReopen` (`[HttpPost("{id:guid}/Sepa/Reopen")]`, `[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]` + resource-based `ExpenseReportOperation.ReopenSepa`) — reopens a SepaSent report back to Approved; `ExpenseReportAuthorizationHandler` now covers `ReopenSepa` for FinanceAdmin/Admin; #985 and #986 are service-layer and view-only changes with no new authorization surface). Previously refreshed 2026-06-10 (via `/freshness-sweep`; #884 added the Survey section — `SurveyController` (`[AllowAnonymous]` — public wizard answering flow), `SurveyAdminController` (`BoardOrAdmin` — authoring/send/results), and `SurveysApiController` (`SurveyApiKeyAuthFilter` — key-authed agent read API); #931 added `ICalFeedApiController` (`[AllowAnonymous]` with secret-in-URL token at `/api/ical/{userId}/{token}.ics`); #930 added `AccountController.GateLogin` (GET/POST, no `[Authorize]` — shared kiosk credential for the gate terminal) and `TicketsGateAdminController` (`TicketAdminOrAdmin` — gate credential management at `/Tickets/Admin/Gate`); `ScannerController`'s class-level policy is now `PolicyNames.ScannerAccess` (a composite assertion that also admits the gate-terminal shared account by well-known id, not just TicketAdmin/Board/Admin roles) — the `ScannerAccess` policy is now listed in §5). Previously refreshed 2026-06-09 (via `/freshness-sweep`, full re-scan; #900 expense travel lines + personal IOU view reshaped the Expenses guard surface — the `ExpenseReportOperationRequirement` resource handler now covers `View` (Detail/Attachment), `Endorse`, `CoordinatorReject`, `Approve`, `FinanceReject`, and `IncludeInSepaPayout`, while all submitter-side actions (Edit, line CRUD including the new `AddMileage`/`AddPerDiem`, Submit, Withdraw, Iban) are gated by inline owner checks (`report.SubmitterUserId != user.Id → Forbid()`); #916 added the barcode scan & search actions (`Barcode`, `Tickets`, `Tickets/Card`) to `ScannerController` and the authenticated `search` / `by-userid` endpoints to `ProfileApiController`; `UsersAdminController` is now gated by a single class-level `HumanAdminBoardOrAdmin` policy with `AdminOnly` action overrides (`RevealIban`, `Audience`, `PurgeHuman`); `TeamController.EditTeam` (POST) gained an in-method `AdminOnly` check driving the `IsSensitive` leave-unchanged guard; the Store landing page admin button group is the first view spelling of `StoreCatalogAdmin`; `_HumanPopover` gained an `AnyAdminRole` camp-visibility flag and the Admin dashboard activity panels an `AdminOnly` view gate). Previously refreshed 2026-06-07 (via `/freshness-sweep`; #899 account-merge consolidation deleted `AdminMergeController` + `AdminDuplicateAccountsController` and replaced them with the single `UsersAdminAccountMergesController` at `/Users/Admin/AccountMerges`; #901 admin route moves gutted `AdminController` down to just the `/Admin` dashboard tile and relocated routes into section controllers — `PurgeHuman` and `RevealIban` now live on `UsersAdminController`, the debug/diagnostics routes were absorbed by `DebugController`, and the role-assignment `AddRole`/`EndRole` guards moved from `ProfileController` to `UsersAdminController`; #898 added the read-only `ShiftsController.Summary*` actions gated by `ShiftDepartmentManager`; #881 name-only access deleted the `IsActiveMember` / `ActiveMemberOrShiftAccess` requirement+handler pairs and `RoleChecks.BypassesMembershipRequirement` — `MembershipRequiredFilter` now gates the app purely on the stored `UserState` (only `Active` reaches it) and routes the rest to name-entry / status-wall / cancel-deletion landings on the new `UserController`). Previously refreshed 2026-06-05 (adds the `CampComplianceAccess` policy + `CampComplianceAccessHandler` and the new `CampComplianceController` for the read-only Barrios compliance matrix, split out of `CampAdminController` so it can be gated more broadly than CampAdmin-only — #894; `RoleAssignmentClaimsTransformation` was re-sourced from `IRoleAssignmentRepository` to `IRoleAssignmentService` in #889, an internal sourcing change with no inventory impact). Previously refreshed 2026-06-04 (full re-scan via `/freshness-sweep`; adds `ProfileApiController.BurnerNameCount`, `ShiftsController.ToggleDay`, the `StoreAdminController` Payments/reconcile actions, and the global `NameRequiredFilter` action filter). Previously refreshed 2026-06-03 (the new `RoleNames.EETeamAdmin` cross-team role, the `TeamOperationRequirement.ManageEarlyEntry` resource operation + `TeamAdminController` EarlyEntry actions, and the build-hash tooltip re-gated to `AdminOnly` / FullAdmin in commit 3c6a878e). Covers every `[Authorize(Policy)]` / `[Authorize(Roles)]` attribute on controllers and actions in `src/Humans.Web/Controllers/` (including `Controllers/Api/` and `Controllers/Mailer/`) and in the section projects under `src/Sections/**/Controllers/` (Agent, Budget, Calendar, Campaigns, CityPlanning, Containers, Events, Expenses, Feedback, Finance, Gate, Governance, Holded, Issues, Notifications, Scanner, Store, Surveys — G5, nobodies-collective/Humans#866), every `RoleChecks.*` / `ShiftRoleChecks.*` invocation across `src/Humans.Web/`, `src/Sections/**`, and `src/Humans.Application/`, every `IAuthorizationService.AuthorizeAsync` call site, every `authorize-policy` TagHelper attribute (implemented by `AuthorizeViewTagHelper` in `src/Humans.UI/TagHelpers/`) and `User.IsInRole` / `Model.X` authorization check across `src/Humans.Web/Views/`, `src/Humans.UI/Views/`, section `Views/`, `src/Humans.Web/ViewComponents/`, and `src/Humans.UI/ViewComponents/`, and every `AuthorizationHandler<T, R>` (and `IAuthorizationHandler`) under `src/Humans.Web/Authorization/`, `src/Humans.UI/Authorization/`, `src/Sections/**/Authorization/`, and `src/Humans.Application/Authorization/`.

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

### Gate Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `GateController` | Class | `TicketAdmin, Admin, Board` OR the gate-terminal shared account (by well-known id) | `PolicyNames.ScannerAccess` (gate admissions terminal at `/Gate` — distinct from the read-only Scanner section: this one decides entry and writes the durable `gate_scan_events` record; the read actions `Index`, `Evaluate`, `Claim` GET, `Search`, `Leaderboard` inherit the class-level policy) |
| `GateController.Decision` | Action | `TicketAdmin, Admin, Board` OR the gate-terminal shared account | `PolicyNames.GateAdmit` (POST — records the admission decision, attributed to the session-claimed scanner; supervisor overrides — too-early / unconfirmed-EE admit, child-without-ID waiver — additionally require the shared override PIN, see §4) |
| `GateController.Claim` (POST) / `ClaimPin` / `EndShift` | Action | `TicketAdmin, Admin, Board` OR the gate-terminal shared account | `PolicyNames.GateAdmit` (claims/releases the scanning session; `ClaimPin` verifies or enrols the staffer's personal 4-digit PIN via `IGateService` with per-target-user `GatePinThrottle` buckets; the verified user id is stamped into the session server-side — never from the request body) |
| `GateController.Admin` (GET/POST) / `SetStaffPin` / `ResetStaffPin` | Action | `TicketAdmin, Admin` | `PolicyNames.TicketAdminOrAdmin` (gate settings + admin PIN enrolment/reset at `/Gate/Admin` — supervisor-authority PINs are never self-enrolled at the kiosk) |
| `GateVendorBackfillAdminController` | Class | `Admin` | `PolicyNames.AdminOnly` (one-off vendor check-in backfill page at `/Gate/Admin/VendorCheckInBackfill` — temp, remove after use; `Index`, `RunOne`, `Run` all inherit — #1080) |

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
| `FinanceController` (`src/Sections/Humans.Finance`) | Class | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` (Holded creditor-account surface — `HoldedAccounts`, `Creditors`, `HoldedSync`; split out of the old single `FinanceController` — #1239, G5 A2) |
| `BudgetAdminController` (`src/Sections/Humans.Budget`, `[Route("Finance")]`) | Class | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` (the other 23 actions of the old `FinanceController` — Budget years/groups/categories/line-items CRUD, cash flow, audit log, ticketing-budget sync; same `/Finance` route prefix, no URL moved — #1239) |

### Holded Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `HoldedController` (`src/Sections/Humans.Holded`) | Class | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` (the ledger mirror's own admin screen at `/Holded` — overview, per-account statement, `SyncNow`/`FullSync` triggers; new — #1261/#1267, same policy as `FinanceController` above) |

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
| `ExpensesController` owner guards | In-method | Submitter-side actions (`Edit` GET/POST, `AddLine`, `UpdateLine`, `RemoveLine`, `AttachFile`, `RemoveAttachment`, `Submit`, `Withdraw`, `Iban` GET/POST) gate on `report.SubmitterUserId != user.Id → Forbid()` (11 sites — the `AddMileage`/`AddPerDiem` travel-line actions added in #900 were removed when travel-line creation was retired) | Inline owner check |

### Store Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `StoreController` | Class | `[Authorize]` (authenticated) | — |
| `StoreController` runtime guards | In-method | `authService.AuthorizeAsync(User, order, OrderOperationRequirement.{View, AddLine, RemoveLine, EditCounterparty, Pay, Delete})` for existing orders and `OrderCreateContext` for Create (both camp orders via `Create` and team orders via `CreateTeamOrder`). Index also seeds `isPrivilegedReader = RoleChecks.CanAdministerStore(User) \|\| RoleChecks.IsTeamsAdmin(User)` (PR #845). | Resource-based (see §6) |
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
| `TeamAdminController.Roster` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` (narrows the class-level coordinator-reachable `ResolveTeamManagementAsync` down to Board-or-Admin only — coordinators don't see the full-name roster) |
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
| `FeedbackController` | Class | `Admin` | `PolicyNames.AdminOnly` — every action inherits it; no per-action attributes (#977) |
| `FeedbackController` runtime guards | In-method | none — the admin-vs-reporter branch was deleted with the reporter view | — |
| `FeedbackApiController` | Class | `[ServiceFilter(typeof(ApiKeyAuthFilter))]` (API-key auth) | `ApiKeyAuthFilter` |

Feedback is retired (nobodies-collective/Humans#977): closed to new reports, no reporter-facing view, and `FeedbackAdmin` alone reaches none of it. `PolicyNames.FeedbackAdminOrAdmin`, `RoleGroups.FeedbackAdminOrAdmin`, and `RoleChecks.IsFeedbackAdmin` were deleted.

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
| `AgentController.Ask` | In-method | `auth.AuthorizeAsync(User, user.Id, [new AgentRateLimitRequirement()])` (requirement instantiated directly — `PolicyNames.AgentRateLimit` was deleted; the requirement/handler are internal to `Humans.Agent` since #1259) | Resource-based (see §6) |
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
| 41 | `var isEventsAdminOrAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.EventsAdminOrAdmin)).Succeeded` | Drives `isEventsAdminOrAdmin` flag for the Events admin sub-dropdowns below |
| 42 | `var isFullAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Drives `isFullAdmin` flag for build-hash tooltip on brand link (commit SHA on hover) — gated to FullAdmin (`AdminOnly`), not `AnyAdminRole` |
| 101 | `authorize-policy="AppAccess"` | City Planning nav link |
| 106 | `authorize-policy="AppAccess"` | Events dropdown (feature-flagged) |
| 112 | `if (isEventsAdminOrAdmin)` | Guide Dashboard / Moderate / Export dropdown items |
| 119 | `if (isEventsAdminOrAdmin)` | Guide Settings / Categories / Venues dropdown items |
| 135 | `authorize-policy="AppAccess"` | Shifts nav link (no separate shift access — merged into `AppAccess`) |
| 138 | `authorize-policy="AppAccess"` | Budget nav link |
| 141 | `authorize-policy="AnyAdminRole"` | Admin nav link (entry to admin shell) |

### Login Partial (`_LoginPartial.cshtml`, now `src/Humans.UI/Views/Shared/_LoginPartial.cshtml`)

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
| `Shifts/Index.cshtml` | 68 | `authorize-policy="ShiftDepartmentManager"` | Dashboard button |
| `Shifts/Index.cshtml` | 69 | `authorize-policy="AdminOnly"` | Settings button |
| `Shifts/NoActiveEvent.cshtml` | 8 | `authorize-policy="AdminOnly"` | "Configure Event Settings" link |
| `ShiftDashboard/Index.cshtml` | 83 | `authorize-policy="ShiftDashboardAccess"` | Voluntell card |
| `ShiftDashboard/Index.cshtml` | 220 | `authorize-policy="ShiftDashboardAccess"` | Volunteer search column |
| `ShiftDashboard/Index.cshtml` | 304, 314 | `authorize-policy="ShiftDashboardAccess"` | Per-row signup-action cells |
| `VolunteerTracking/_VolunteerHeatmap.cshtml` | 9 | `(await AuthService.AuthorizeAsync(User, PolicyNames.VolunteerTrackingWrite)).Succeeded` | Drives `canWrite` flag for cell-level write actions |
| `VolunteerTracking/_VolunteerUnbookedHeatmap.cshtml` | 8 | `(await AuthService.AuthorizeAsync(User, PolicyNames.VolunteerTrackingWrite)).Succeeded` | Drives `canWrite` flag for cell-level write actions |

### Profile Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Profile/Index.cshtml` | 18 | `authorize-policy="HumanAdminBoardOrAdmin"` | "Admin" link to AdminDetail |
| `Profile/Index.cshtml` | 73 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminBoardOrAdmin)).Succeeded` | `ProfileCardViewMode.Admin` vs `Public` for non-own profiles |
| `Profile/Emails.cshtml` | 18 | `(await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Admin-only email management controls |
| `UsersAdmin/AdminDetail.cshtml` | 14 | `var isAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Drives `isAdmin` flag for Admin-only data blocks |

### Board / Onboarding Review Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Governance/BoardVoting/Detail.cshtml` | 102 | `authorize-policy="BoardOnly"` | Vote casting card |

### Team Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Team/Index.cshtml` | 21 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminBoardOrAdmin)).Succeeded` | "Summary" + "Sync Status" toolbar buttons on the Teams landing page |
| `Team/Summary.cshtml` | 22 | `authorize-policy="BoardOrAdmin"` | "Create Team" button |
| `Team/Summary.cshtml` | 50 | `authorize-policy="BoardOrAdmin"` | Actions column header |
| `Team/_AdminTeamRow.cshtml` | 44 | `(await AuthService.AuthorizeAsync(User, PolicyNames.BoardOrAdmin)).Succeeded` | Pending-shift-signup badge link |
| `Team/_AdminTeamRow.cshtml` | 96 | `authorize-policy="BoardOrAdmin"` | Actions column cell (Edit/Deactivate buttons) |
| `Team/EditTeam.cshtml` | 81 | `authorize-policy="AdminOnly"` | "Sensitive team" checkbox |
| `Team/Details.cshtml` | 315 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminOrAdmin)).Succeeded` | Drives `canOpenStore` flag (OR'd with coordinator-of-active-top-level-department) for the "Open store" button |
| `Team/Details.cshtml` | 386 | `authorize-policy="@PolicyNames.BoardOrAdmin"` | "Roster" link (matches `TeamAdminController.Roster`'s action-level policy) |

### Camp Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Camp/Index.cshtml` | 12 | `authorize-policy="CampAdminOrAdmin"` | "Camp Admin" link |
| `CampAdmin/Index.cshtml` | 460 | `authorize-policy="AdminOnly"` | Danger Zone card (Delete Camp) |

### Ticket Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Ticket/Index.cshtml` | 248 | `authorize-policy="TicketAdminOrAdmin"` | "Sync Now" form |
| `Ticket/Index.cshtml` | 254 | `authorize-policy="AdminOnly"` | "Full Re-sync" form |
| `Ticket/Index.cshtml` | 262 | `authorize-policy="TicketAdminOrAdmin"` | Export link |
| `Ticket/_TicketNav.cshtml` | 26 | `authorize-policy="AdminOnly"` | "Backfill" tab |

### Google Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Google/_SyncTabContent.cshtml` | 8 | `(await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Drives `canExecuteActions` flag for execute-action buttons on the Google sync tab |
| `Google/_SyncTabContent.cshtml` | 9 | `(await AuthService.AuthorizeAsync(User, PolicyNames.BoardOrAdmin)).Succeeded` | Drives `canViewAudit` flag for the audit-log link on the Google sync tab |

### Campaign Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Campaign/Detail.cshtml` | 24 | `var isAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Drives admin-gated buttons below |
| `Campaign/Detail.cshtml` | 25 | `var canGenerateCodes = (await AuthService.AuthorizeAsync(User, PolicyNames.TicketAdminOrAdmin)).Succeeded` | Drives "Generate Codes" form |

### Admin / Store Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Admin/Index.cshtml` | 125 | `authorize-policy="AdminOnly"` | Recent-activity / dashboard split-panels on the admin landing page |
| `Store/Index.cshtml` | 12 | `authorize-policy="StoreCatalogAdmin"` | Catalog / Summary / Payments admin button group on the Store landing page |

### Shared Components

| View | Line | Check | Controls |
|---|---|---|---|
| `Shared/Components/ProfileCard/Default.cshtml` | 30 | `(await AuthService.AuthorizeAsync(User, PolicyNames.HumanAdminBoardOrAdmin)).Succeeded` | Admin / Board view of profile card |
| `Shared/_HumanPopover.cshtml` | 7 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminBoardOrAdmin)).Succeeded` | Drives `canSeeHiddenTeams` flag (hidden-team list in popover) |
| `Shared/_HumanPopover.cshtml` | 11 | `(await AuthService.AuthorizeAsync(User, PolicyNames.AnyAdminRole)).Succeeded` | Drives `canSeeCamp` flag (camp membership in popover) |
| `Shared/_HumanPopover.cshtml` | 20 | `(await AuthService.AuthorizeAsync(User, PolicyNames.HumanAdminBoardOrAdmin)).Succeeded` | HumanAdmin/Board/Admin popover details (preferred-language flag) |
| `WidgetGallery/Index.cshtml` | 1171 / 1176 | `authorize-policy="@PolicyNames.AdminOnly"` / `authorize-policy="DefinitelyNotARealPolicyName"` | Documentation/demo of the TagHelper (not production gating) |
| `AuthorizeViewTagHelper` (`src/Humans.UI/TagHelpers/AuthorizeViewTagHelper.cs`) | — | `IAuthorizationService.AuthorizeAsync(user, Policy)` | Backs every `authorize-policy="..."` attribute above |
| `AdminSidebarViewComponent` | line 28 | `IAuthorizationService.AuthorizeAsync(HttpContext.User, null, item.Policy)` | Filters /Admin sidebar items per policy |

---

## 3. Same-Rule-Different-Spelling Table

Post Phase-1 retirement, controllers and views express the same authorization rule by referencing the same `PolicyNames` constant — the controller via the `[Authorize(Policy = ...)]` attribute, the view via the `authorize-policy="..."` TagHelper attribute (or `(await AuthService.AuthorizeAsync(User, PolicyNames.X)).Succeeded` when a boolean is needed). The legacy `RoleChecks.*` / `ShiftRoleChecks.*` helpers are no longer invoked from any view, and the Events Guide section's controllers and `_Layout.cshtml` dropdown both resolve through `PolicyNames.EventsAdminOrAdmin`.

| Rule | Controller Spelling | View Spelling |
|---|---|---|
| Admin only | `[Authorize(Policy = PolicyNames.AdminOnly)]` | `authorize-policy="AdminOnly"` |
| Any admin role (admin shell) | `[Authorize(Policy = PolicyNames.AnyAdminRole)]` | `authorize-policy="AnyAdminRole"` |
| Board or Admin | `[Authorize(Policy = PolicyNames.BoardOrAdmin)]` | `authorize-policy="BoardOrAdmin"` |
| TeamsAdmin/Board/Admin | `[Authorize(Policy = PolicyNames.TeamsAdminBoardOrAdmin)]` | `authorize-policy="TeamsAdminBoardOrAdmin"` |
| TeamsAdmin or Admin | (no current controller spelling — registered but only referenced from a view) | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminOrAdmin)).Succeeded` (Team/Details.cshtml "Open store" flag) |
| TicketAdmin/Board/Admin | `[Authorize(Policy = PolicyNames.TicketAdminBoardOrAdmin)]` | `authorize-policy="TicketAdminBoardOrAdmin"` |
| TicketAdmin or Admin | `[Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]` | `authorize-policy="TicketAdminOrAdmin"` |
| Scanner access (roles + gate terminal) | `[Authorize(Policy = PolicyNames.ScannerAccess)]` | (no current view spelling) |
| Gate admit (gate write actions) | `[Authorize(Policy = PolicyNames.GateAdmit)]` | (no current view spelling) |
| CampAdmin or Admin | `[Authorize(Policy = PolicyNames.CampAdminOrAdmin)]` | `authorize-policy="CampAdminOrAdmin"` |
| HumanAdmin/Board/Admin | `[Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]` | `authorize-policy="HumanAdminBoardOrAdmin"` |
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
| Resource: store order | `authService.AuthorizeAsync(User, order, OrderOperationRequirement.{View, Create, AddLine, RemoveLine, EditCounterparty, Pay, Delete})` (and `OrderCreateContext` for Create) | `Model.CanManageByCounterparty` / per-order flags (view-model) |
| Resource: expense report | `authService.AuthorizeAsync(User, report, new ExpenseReportOperationRequirement(ExpenseReportOperation.X))` — `View`, `Endorse`, `CoordinatorReject`, `Approve`, `FinanceReject` (submitter-side actions use inline `SubmitterUserId` owner checks instead) | `Model.CanX` (view-model) |
| Resource: IBAN access | `IbanAccessHandler` / `IbanAccessRequirement` are **registered but have no production call site** (only `IbanAccessHandlerTests` exercise them). `UsersAdminController.RevealIban` is gated by `[Authorize(Policy = PolicyNames.AdminOnly)]`; expense-report IBAN views show masked self-IBAN with no resource check. | (none today) |
| Resource: issue handle | `_authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)` | `Model.CanHandle` (view-model) |
| Resource: user-email edit | `_authorizationService.AuthorizeAsync(User, userId, UserEmailOperations.Edit)` | (no view spelling) |
| Resource: agent rate-limit | `auth.AuthorizeAsync(User, user.Id, [new AgentRateLimitRequirement()])` (requirement passed directly — no named `PolicyNames` constant) | (no view spelling) |
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
| `ExpensesController` | Submitter-side actions (Edit, line CRUD, Submit, Withdraw, Iban) | Inline owner check `report.SubmitterUserId != user.Id → Forbid()` (#900) |
| `TeamController` | `EditTeam` (POST) `IsSensitive` flag | `authorizationService.AuthorizeAsync(User, PolicyNames.AdminOnly)` — non-Admin posts leave `IsSensitive` unchanged |
| `StoreController` | Order CRUD/pay | `_authService.AuthorizeAsync(User, order, OrderOperationRequirement.*)` (resource-based) |
| `IssuesController` | All mutating actions | `_authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)` (resource-based) |
| `CityPlanningController` / `CityPlanningApiController` | All actions except `Index`/`GetState` | `RoleChecks.IsCampAdmin(User)` and lead-of-camp checks; three API endpoints also call `_authorizationService.AuthorizeAsync` |
| `GovernanceBoardVotingController` | `Detail` | `RoleChecks.IsAdmin(User)` drives the admin view-model flag (Finalize affordance) — the `Finalize` POST itself is attribute-gated `AdminOnly` |
| `UsersAdminController.AddRole/EndRole` | After `[Authorize(Policy)]` attribute | `_authorizationService.AuthorizeAsync(User, roleName, RoleAssignmentOperationRequirement.Manage)` enforces the role-list filter |
| `ProfileController` email-edit endpoints (18 actions) | After class-level `[Authorize]` | `_authorizationService.AuthorizeAsync(User, userId, UserEmailOperations.Edit)` (resource-based) |
| `TicketController.Index` | After class-level policy | `RoleChecks.CanAccessFinance(User)` toggles finance-only metrics |
| `MembershipRequiredFilter` | All authenticated requests | Gates the app purely on stored `UserState` (stamped on the principal by `RoleAssignmentClaimsTransformation`): only `Active` reaches the app; `DeletePending` → `/User/Deletion`, Suspended/AdminSuspended/Rejected/Deleted/Merged → `/User/Status`, Bare/unseeded → `/OnboardingWidget`. Roles do not bypass the gate. Exempt controllers (`Account`, `OnboardingWidget`, `Profile`, `Consent`, `User`, `Language`, `Guest`, `GovernanceApplications`, `Issues`, `Notifications`, `Survey`) and `[AllowAnonymous]` pass through. Replaced the deleted `IsActiveMember` / `ActiveMemberOrShiftAccess` requirement+handler pairs and `RoleChecks.BypassesMembershipRequirement` (#881). |
| `NameRequiredFilter` | All requests | Global action filter (registered in `Program.cs` before `MembershipRequiredFilter`). Redirects any authenticated user with no real `BurnerName` to the name form; never blocks sign-in (only redirects). Exempt controllers (`Account`, `Language`), exempt actions (`OnboardingWidget/Names`, `Home/Error`, `Home/Privacy`), and `[AllowAnonymous]` pass through. |
| `HangfireAuthorizationFilter` | Hangfire dashboard | `RoleChecks.IsAdmin(User)` |
| `AgentController.Ask` | Per-request | `auth.AuthorizeAsync(User, user.Id, [new AgentRateLimitRequirement()])` (resource-based; requirement instantiated directly rather than via a named policy — #1259) |
| `GateController.Decision` | Supervisor overrides (too-early / unconfirmed-EE admit, child-without-ID waiver) | Shared override PIN from `Gate:SupervisorPin` config, verified server-side (`SupervisorPinValid` — SHA-256 fixed-time compare) and brute-force-throttled via `GatePinThrottle` (one shared bucket for the terminal: 5 tries / 15 min); fail-closed when no PIN is configured. The PIN authorizes but cannot attribute — the event records the gate account. |
| `GateController.Claim` (POST) / `ClaimPin` | Claimant identity | Claimant must be a real active member (`UserService.GetUserInfoAsync(...).IsActive` — guards a direct POST with an arbitrary/inactive id); `ClaimPin` verifies (`IGateService.VerifyPinAsync`) or enrols (`SetOwnPinAsync`) the staffer's personal PIN with per-target-user `GatePinThrottle` buckets, then stamps the verified user id into the session server-side (attribution can't be forged from the request body) |

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
| `TeamsAdminOrAdmin` | TeamsAdmin, Admin | `PolicyNames.TeamsAdminOrAdmin` (registered in `AuthorizationPolicyExtensions` but currently only referenced from `Team/Details.cshtml`'s "Open store" boolean — no controller attribute uses it yet) |
| `CampAdminOrAdmin` | CampAdmin, Admin | `PolicyNames.CampAdminOrAdmin`, `RoleChecks.IsCampAdmin` |
| `CampComplianceAccess` | CampAdmin, Admin OR any team/sub-team coordinator | `PolicyNames.CampComplianceAccess` (composite — `CampComplianceAccessHandler`) |
| `TicketAdminBoardOrAdmin` | TicketAdmin, Admin, Board | `PolicyNames.TicketAdminBoardOrAdmin`, `RoleChecks.CanAccessTickets` |
| `TicketAdminOrAdmin` | TicketAdmin, Admin | `PolicyNames.TicketAdminOrAdmin`, `RoleChecks.CanManageTickets` |
| `ScannerAccess` | TicketAdmin, Admin, Board OR `SystemUserIds.GateTerminal` (by NameIdentifier claim) | `PolicyNames.ScannerAccess` (composite assertion — gate-terminal account admitted by id, not by role) |
| `GateAdmit` | TicketAdmin, Admin, Board OR `SystemUserIds.GateTerminal` (by NameIdentifier claim) | `PolicyNames.GateAdmit` (composite assertion — gate write actions; same principals as `ScannerAccess` today, kept separate so the write path never rides the read-only Scanner gate and the two can diverge) |
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

### Notes on Policy Design

- `ShiftDashboardAccess` and `ShiftDepartmentManager` are intentionally distinct: dashboard access is role-list-based, department manager additionally permits any team manager/coordinator (composite via `IsAnyTeamManagerOrCoordinatorHandler`).
- `AppAccess` is the single nav-visibility gate: `UserState == Active` (the user entered their legal name). A plain `RequireAssertion` — no custom requirement/handler. It replaced the former `IsActiveMember` / `ActiveMemberOrShiftAccess` policies (and there is no separate shift access).
- `CampComplianceAccess` is deliberately broader than `CampAdminOrAdmin`: it short-circuits for CampAdmin/Admin and otherwise admits any team/sub-team coordinator (composite via `CampComplianceAccessHandler`, reusing the same `IShiftManagementService.GetCoordinatorTeamIdsAsync` lookup as `IsAnyTeamManagerOrCoordinatorHandler`). It gates only the read-only Barrios compliance matrix; the camp-management surface in `CampAdminController` stays CampAdmin-only.
- `HumanAdminOnly` is a composite policy used for the nav "Humans" link that only shows when the user has HumanAdmin but not the broader Board/Admin access.
- `MedicalDataViewer` is a data-access policy, not a page-access policy. It controls whether medical fields are visible within pages the user already has access to.
- `GateAdmit` is deliberately a twin of `ScannerAccess` (same assertion body): the durable gate write surface (`/Gate/Decision`, `/Gate/Claim` POST, `/Gate/ClaimPin`, `/Gate/EndShift`) must never inherit a future loosening of the read-only Scanner gate. Supervisor overrides inside `Decision` are a second factor on top of the policy — the shared `Gate:SupervisorPin`, not an identity check (see §4).
- `AnyAdminRole` gates the admin-shell entry point (`/Admin`). Sidebar items inside the shell are filtered per-item by `AdminSidebarViewComponent` against each item's policy. The role list mirrors the top-nav check in `_Layout.cshtml` and includes the grantable `CantinaAdmin` role added with the Cantina coordinator surface (feature #36).
- Object-relative policies (coordinator of specific team, camp lead of specific camp, camp-event submitter, budget category for coordinator's department, manageable role for HumanAdmin, expense reports, store orders, containers, issues, user-email edits, agent rate-limit) are implemented as resource-based authorization handlers — see §6.

---

## 6. Resource-Based Authorization Handlers

Resource-based authorization handlers are subclasses of `AuthorizationHandler<TRequirement, TResource>` (or `AuthorizationHandler<TRequirement>` / `IAuthorizationHandler` directly when the same handler covers multiple resource shapes) that evaluate whether a user can perform an operation on a specific resource instance. They are invoked via `IAuthorizationService.AuthorizeAsync(User, resource, requirement)` from controllers (or controller base classes).

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `TeamAuthorizationHandler` | `TeamOperationRequirement` (`ManageCoordinators`, `ManageEarlyEntry`) | `TeamInfo` | `src/Humans.Web/Authorization/Requirements/TeamAuthorizationHandler.cs` — Admin/TeamsAdmin/Board: any team, any op; `EETeamAdmin`: any team for `ManageEarlyEntry` only; team coordinator: own team only (both ops) |
| `CampAuthorizationHandler` | `CampOperationRequirement` (`Manage`, `SubmitEvent`) | `CampLookup` / `Camp` entity / camp id (`Guid`) | `src/Humans.Web/Authorization/Requirements/CampAuthorizationHandler.cs` |
| `BudgetAuthorizationHandler` | `BudgetOperationRequirement` (`Edit`) | `BudgetCategorySnapshot` | `src/Sections/Humans.Budget/Authorization/BudgetAuthorizationHandler.cs` (moved with the Budget section — #1263, G5 overnight batch; registered in `Section.cs`) |
| `ContainerAuthorizationHandler` | `ContainerOperationRequirement` (`Manage`, `Place`) | `ContainerAuthorizationTarget` | `src/Sections/Humans.Containers/Authorization/ContainerAuthorizationHandler.cs` (moved with the Containers section — #1239, G5 A2; registered in the section's own `Section.cs` DI, no longer in `AuthorizationPolicyExtensions`) |
| `OrderAuthorizationHandler` | `OrderOperationRequirement` (`View`, `Create`, `AddLine`, `RemoveLine`, `EditCounterparty`, `Pay`, `Delete`) | `OrderDto` / `OrderCreateContext` | `src/Sections/Humans.Store/Authorization/OrderAuthorizationHandler.cs` — renamed from `StoreOrderAuthorizationHandler`/`StoreOrderOperationRequirement`/`StoreOrderCreateContext` when Store dropped its now-redundant prefix inside its own project (moved with the Store section — #1223, G5 pilot; registered in `Section.cs`) |
| `ExpenseReportAuthorizationHandler` | `ExpenseReportOperationRequirement` (`View`, `Edit`, `Submit`, `Withdraw`, `Endorse`, `CoordinatorReject`, `Approve`, `FinanceReject`, `CategoryOverride`) | `ExpenseReportDto` | `src/Sections/Humans.Expenses/Authorization/ExpenseReportAuthorizationHandler.cs` (moved with the Expenses section — #1240, G5 A3; registered in `Section.cs`) |
| `IbanAccessHandler` | `IbanAccessRequirement` | (intrinsic — `TargetUserId` / `ReportId` / `IsAdminPageContext` fields on requirement) | `src/Sections/Humans.Expenses/Authorization/IbanAccessHandler.cs` (moved with the Expenses section — #1240) — **registered in DI but no production call site today** (only `IbanAccessHandlerTests`); `UsersAdminController.RevealIban` uses `[Authorize(Policy = AdminOnly)]` instead. |
| `IssuesAuthorizationHandler` | `IssuesOperationRequirement` (`Handle`) | `IssueDetail` | `src/Sections/Humans.Issues/Authorization/IssuesAuthorizationHandler.cs` (moved with the Issues section — #1263, G5 overnight batch; registered in `Section.cs`) |
| `UserEmailAuthorizationHandler` | `UserEmailOperationRequirement` (`Edit`) | `Guid` (target user id) | `src/Humans.Web/Authorization/Requirements/UserEmailAuthorizationHandler.cs` |
| `RoleAssignmentAuthorizationHandler` | `RoleAssignmentOperationRequirement` (`Manage`) | `string` (roleName) | `src/Sections/Humans.Auth/Authorization/RoleAssignmentAuthorizationHandler.cs` |
| `AgentRateLimitHandler` | `AgentRateLimitRequirement` | `Guid` (user id) | `src/Sections/Humans.Agent/Authorization/AgentRateLimitHandler.cs` (moved with the Agent section — #1259, G5 A4b; the requirement is now instantiated directly at its one call site instead of resolved via a `PolicyNames` constant — no longer a registered named policy, so it no longer appears in §5) |

Composite (non-resource) handlers registered alongside the above:

| Handler | Requirement | Path |
|---|---|---|
| `HumanAdminOnlyHandler` | `HumanAdminOnlyRequirement` | `src/Humans.Web/Authorization/Requirements/HumanAdminOnlyHandler.cs` |
| `IsAnyTeamManagerOrCoordinatorHandler` | `IsAnyTeamManagerOrCoordinatorRequirement` | `src/Humans.Web/Authorization/Requirements/IsAnyTeamManagerOrCoordinatorHandler.cs` |
| `CampComplianceAccessHandler` | `CampComplianceAccessRequirement` | `src/Humans.Web/Authorization/Requirements/CampComplianceAccessHandler.cs` (short-circuits for CampAdmin/Admin; else admits any team/sub-team coordinator via `IShiftManagementService.GetCoordinatorTeamIdsAsync`) |

### `IAuthorizationService.AuthorizeAsync` Call Sites

| File | Line | Call |
|---|---|---|
| `src/Humans.Web/Controllers/HumansTeamControllerBase.cs` | 24 | `AuthorizeAsync(User, team, TeamOperationRequirement.ManageCoordinators)` (`ResolveTeamManagementAsync`) |
| `src/Humans.Web/Controllers/HumansTeamControllerBase.cs` | 37 | `AuthorizeAsync(User, team, TeamOperationRequirement.ManageEarlyEntry)` (`ResolveEarlyEntryManagementAsync`) |
| `src/Humans.Web/Controllers/TeamController.cs` | 162 | `AuthorizeAsync(User, teamInfo, TeamOperationRequirement.ManageEarlyEntry)` (drives `CanManageEarlyEntry` view-model flag on team details) |
| `src/Humans.Web/Controllers/TeamController.cs` | 681 | `AuthorizeAsync(User, PolicyNames.AdminOnly)` (EditTeam POST — `IsSensitive` leave-unchanged guard for non-Admin editors) |
| `src/Humans.UI/Controllers/HumansCampControllerBase.cs` | 24 | `AuthorizeAsync(User, campId, CampOperationRequirement.Manage)` (moved from `src/Humans.Web/Controllers/` to `Humans.UI` — #1235) |
| `src/Humans.UI/Controllers/HumansCampControllerBase.cs` | 58 | `AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` |
| `src/Humans.UI/Controllers/HumansCampControllerBase.cs` | 88 | `AuthorizeAsync(User, camp, CampOperationRequirement.SubmitEvent)` |
| `src/Sections/Humans.Budget/Controllers/BudgetController.cs` | 30 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` (moved with the Budget section — #1263; line numbers shifted +1 by an added `using`) |
| `src/Sections/Humans.Budget/Controllers/BudgetController.cs` | 93 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` |
| `src/Sections/Humans.Budget/Controllers/BudgetController.cs` | 113 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` |
| `src/Sections/Humans.Budget/Controllers/BudgetController.cs` | 119 | `AuthorizeAsync(User, detail.Category, BudgetOperationRequirement.Edit)` |
| `src/Sections/Humans.Budget/Controllers/BudgetController.cs` | 230 | `AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)` |
| `src/Sections/Humans.Containers/Controllers/ContainerController.cs` | 23 | `AuthorizeAsync(User, target, requirement)` (private helper, called from six sites in the controller; moved with the Containers section — #1239) |
| `src/Sections/Humans.Expenses/Controllers/ExpensesController.cs` | 163, 512, 558, 578, 623, 644 | `AuthorizeAsync(User, report, new ExpenseReportOperationRequirement(ExpenseReportOperation.X))` — `View` (Detail 163, Attachment 512), `Endorse` 558, `CoordinatorReject` 578, `Approve` 623, `FinanceReject` 644 (moved with the Expenses section — #1240; line numbers shifted down ~44 lines by #1265's mileage/per-diem line-creation retirement) |
| `src/Sections/Humans.Expenses/Controllers/ExpensesController.cs` | 185 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` (drives the `isFinanceAdmin` flag on report Detail — Holded creditor-account binding UI for finance admins) |
| `src/Sections/Humans.Store/Controllers/StoreController.cs` | 55, 77, 80, 81, 82, 104, 121, 139, 179, 197, 226, 256, 283, 308 | `AuthorizeAsync(User, order/resource, OrderOperationRequirement.X)` (and `OrderCreateContext` / `OrderLineContext` for Create at 104 and line-deadline-aware AddLine/RemoveLine at 256/283; moved with the Store section — #1223, requirement/context types renamed off their `StoreOrder*` prefix in the same move) |
| `src/Sections/Humans.Issues/Controllers/IssuesController.cs` | 193, 263, 309, 336, 363, 388 | `AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)` (moved with the Issues section — #1263; line numbers shifted +2 by an added `using`) |
| `src/Sections/Humans.CityPlanning/Controllers/CityPlanningApiController.cs` | 274, 299, 337 | `AuthorizeAsync(User, ...)` (resource-based; moved with the CityPlanning section — #1263; line numbers shifted -1) |
| `src/Humans.Web/Controllers/ProfileController.cs` | 718, 751, 796, 838, 875, 912, 949, 969, 1013, 1092, 1108, 1134, 1167, 1193, 1219, 1395, 1421, 1465 | `AuthorizeAsync(User, userId, UserEmailOperations.Edit)` (18 email-edit endpoints; line numbers shifted +1 by an added `using`) |
| `src/Humans.Web/Controllers/ProfileController.cs` | 1877 | `AuthorizeAsync(User, PolicyNames.TicketAdminBoardOrAdmin)` (onsite-chip visibility gate) |
| `src/Humans.Web/Controllers/ProfileController.cs` | 1973 | `AuthorizeAsync(User, PolicyNames.PrivilegedSignupApprover)` (inside `BuildSentMessagesContextAsync` private helper — gates whether a non-own-profile viewer sees the "sent messages" panel on the profile page; admits coordinators or `PrivilegedSignupApprover` role) |
| `src/Humans.Web/Controllers/UsersAdminController.cs` | 303 | `AuthorizeAsync(User, model.RoleName, RoleAssignmentOperationRequirement.Manage)` (AddRole — moved off `ProfileController` in #901) |
| `src/Humans.Web/Controllers/UsersAdminController.cs` | 341 | `AuthorizeAsync(User, roleAssignment.RoleName, RoleAssignmentOperationRequirement.Manage)` (EndRole — moved off `ProfileController` in #901) |
| `src/Sections/Humans.Agent/Controllers/AgentController.cs` | 54 | `AuthorizeAsync(User, user.Id, [new AgentRateLimitRequirement()])` (moved with the Agent section — #1259; requirement instantiated directly, no longer via `PolicyNames.AgentRateLimit`) |
| `src/Humans.UI/TagHelpers/AuthorizeViewTagHelper.cs` | 54 | `AuthorizeAsync(user, Policy)` (driver of `<authorize-policy>` view tags; relocated from `src/Humans.Web/TagHelpers/` by #1220) |
| `src/Humans.Web/ViewComponents/AdminSidebarViewComponent.cs` | 28 | `AuthorizeAsync(HttpContext.User, null, item.Policy)` (filters admin sidebar) |

---

## 7. Notes / Known Deviations

- **No `[Authorize(Roles = ...)]` attributes remain anywhere in `src/`** — every controller/action `[Authorize]` attribute now references a `PolicyNames` constant or is a bare authenticated/`[AllowAnonymous]` marker (verified 2026-05-29). `DevSeedController.ResetDashboard`, formerly the last `[Authorize(Roles = RoleNames.Admin)]` holdout, now uses `[Authorize(Policy = PolicyNames.AdminOnly)]`.
- **`ScannerController` uses `PolicyNames.ScannerAccess`**, not `TicketAdminBoardOrAdmin` — the `ScannerAccess` policy is a composite assertion that additionally admits the shared gate-terminal account by its well-known `SystemUserIds.GateTerminal` NameIdentifier claim so the kiosk session can scan without holding any role (added as part of #930 gate-terminal login).
- **The Gate section (`/Gate`) splits read from write**: `GateController` reads under `ScannerAccess`, but every state-changing action (`Decision`, `Claim` POST, `ClaimPin`, `EndShift`) is gated by the separate `GateAdmit` policy (#1066). Scan attribution is session-based, stamped server-side after an active-member + personal-PIN check; supervisor overrides use the shared `Gate:SupervisorPin` config value (server-verified, throttled, fail-closed) — the PIN authorizes but never attributes. `GateController.Search` is a deliberately name-only, masked-email people search so the route-locked kiosk never exposes the broader `/api/profiles/search` surface.
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
