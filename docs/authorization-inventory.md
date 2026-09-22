# Authorization Inventory

**Phase 1 is complete:** every canonical policy in §5 is registered in `AuthorizationPolicyExtensions.AddHumansAuthorizationPolicies`, all controllers use `[Authorize(Policy = PolicyNames.X)]`, the `authorize-policy` TagHelper resolves through `IAuthorizationService`, and views no longer call `RoleChecks.*` / `ShiftRoleChecks.*` directly. **Phase 2 (resource-based authorization)** has shipped multiple vertical slices — see §6. **Phase 3 (service-layer enforcement) is cancelled.**

Covers every `[Authorize(Policy)]` / `[Authorize(Roles)]` attribute on controllers and actions across `src/Humans.Web/Controllers/` and every section project under `src/Sections/*/Controllers/`, every `RoleChecks.*` / `ShiftRoleChecks.*` invocation across `src/Humans.Web/`, `src/Sections/**`, and `src/Humans.Base/`, every `IAuthorizationService.AuthorizeAsync` call site, every `authorize-policy` TagHelper attribute (implemented by `AuthorizeViewTagHelper` in `src/Humans.Base/TagHelpers/`) and `User.IsInRole` / `Model.X` authorization check across views and view components, and every `AuthorizationHandler<T, R>` (and `IAuthorizationHandler`) under `src/Humans.Web/Authorization/`, `src/Humans.Base/Authorization/`, and `src/Sections/**/Authorization/`. (`Humans.Domain`, `Humans.Application`, and `Humans.UI` no longer exist; their non-Razor plumbing lives in `Humans.Base`, under `Humans.Base.*` namespaces — see §6.)

The `Source` column reflects the constant referenced in the attribute as it appears in the code today.

---

## Per-Section Inventories

Each section's controller authorization map (and any resource-based authorization handler it
owns) lives in its own project at `src/Sections/Humans.<Section>/Docs/authorization.md` —
regenerated per-section so a change inside one section only touches that section's file. This
global file keeps only what has no owning section project: the `/Admin` dashboard shell and
other misc `Humans.Web` controllers, the View Authorization Map, the cross-cutting reference
tables (Same-Rule-Different-Spelling, Enforcement Gaps, Canonical Policy Names), the composite
(non-resource) authorization handlers, and the full `AuthorizeAsync` call-site table.

---

## 1. Controller Authorization Map

### Admin Section

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `AdminController` (`src/Humans.Web`) | Class | `[Route("Admin")]` only — no class-level `[Authorize]` | — |
| `AdminController.Index` | Action | `Admin, Board, HumanAdmin, TeamsAdmin, CampAdmin, TicketAdmin, EventsAdmin, FeedbackAdmin, FinanceAdmin, StoreAdmin, CantinaAdmin, RideshareAdmin, NoInfoAdmin, VolunteerCoordinator, ConsentCoordinator` | `PolicyNames.AnyAdminRole` (the only action left on the gutted dashboard controller) |

`/Admin/*` is a nav holder, not a section — each admin surface's controller lives in the section it acts on (see Per-Section Inventories above). `WidgetGalleryController` moved to `Humans.Debug` — see that section's authorization doc.

### About / Home / Account / Misc

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `AboutController` (`src/Humans.Web`) | Class | (no class-level `[Authorize]`) | — |
| `AboutController.Staff` | Action | `[Authorize]` (authenticated) | — |
| `HomeController` (`src/Humans.Web`) | Class | (no class-level `[Authorize]`) | — |
| `AccountController` (`src/Humans.Web`) | Class | (no class-level `[Authorize]`) | — |
| `AccountController.GateLogin` (GET/POST) | Action | (no `[Authorize]`) | — (shared kiosk credential login at `/Account/GateLogin`; IP-throttled via `GateLoginThrottle`; never gated by role — the gate-terminal account holds no roles) |
| `LanguageController` (`src/Humans.Web`) | Class | (no class-level `[Authorize]`) | — |

`WelcomeController` and `GuestController` moved to `Humans.Onboarding` (#1091) — see that section's authorization doc. `ColorPaletteController` moved to `Humans.Debug`, `TicketsGateAdminController` moved to `Humans.Tickets` — see those sections' authorization docs.

### Public / API

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `TimezoneApiController` (`src/Humans.Web`) | Class | (no class-level `[Authorize]`) | — |
| `HangfireAuthorizationFilter` (`src/Humans.Web`) | Filter | `RoleChecks.IsAdmin(User)` | Admin only |

---

## 2. View Authorization Map

Views express authorization four ways today:

1. **`authorize-policy="PolicyName"` TagHelper attribute** — the dominant pattern. Resolves through `IAuthorizationService.AuthorizeAsync(User, policyName)` via `AuthorizeViewTagHelper`. Hides the element when the policy fails.
2. **`(await AuthService.AuthorizeAsync(User, PolicyNames.X)).Succeeded`** — used when a view needs the boolean for branching, multi-use within the page, or to drive a `var` flag rather than gate one element. Requires `@inject IAuthorizationService AuthService`.
3. **`User.IsInRole(RoleNames.X)` direct calls** — no longer present in any view file.
4. **`Model.CanX` / `Model.IsX` view-model properties** — for resource-relative checks (coordinator-of-this-team, lead-of-this-camp, can-edit-this-budget) and for status-driven UI (suspended badge, approved badge, etc.). The view does not know about roles; the controller / view-model author resolved authorization upstream.

`RoleChecks.*` and `ShiftRoleChecks.*` are no longer invoked from any view file (Phase 1 retirement complete).

### Nav Layout (`_Layout.cshtml`)

| Line | Check | Controls |
|---|---|---|
| 41 | `var isEventsAdminOrAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.EventsAdminOrAdmin)).Succeeded` | Drives `isEventsAdminOrAdmin` flag for the Events admin sub-dropdowns below |
| 42 | `var isFullAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Drives `isFullAdmin` flag for build-hash tooltip on brand link (commit SHA on hover) — gated to FullAdmin (`AdminOnly`), not `AnyAdminRole` |
| 101 | `authorize-policy="AppAccess"` | City Planning nav link |
| 106 | `authorize-policy="AppAccess"` | Events dropdown (feature-flagged) |
| 112 | `if (isEventsAdminOrAdmin)` | Guide Dashboard / Moderate / Export dropdown items |
| 119 | `if (isEventsAdminOrAdmin)` | Guide Settings / Categories / Venues dropdown items |
| 143 | `authorize-policy="AppAccess"` | Shifts nav link (no separate shift access — merged into `AppAccess`) |
| 146 | `authorize-policy="AppAccess"` | Budget nav link |
| 149 | `authorize-policy="AnyAdminRole"` | Admin nav link (entry to admin shell) |

### Login Partial (`_LoginPartial.cshtml`, `src/Humans.Web/Views/Shared/_LoginPartial.cshtml`)

| Line | Check | Controls |
|---|---|---|
| 49 | `authorize-policy="AppAccess"` | Governance link in profile dropdown |

### Guide Layout (`_GuideLayout.cshtml`, `src/Sections/Humans.Guide/Views/Shared/_GuideLayout.cshtml`)

| Line | Check | Controls |
|---|---|---|
| 40 | `authorize-policy="AdminOnly"` | "Refresh from GitHub" button |

### Shift Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Shifts/Index.cshtml` (`src/Sections/Humans.Shifts`) | 67 | `authorize-policy="ShiftDepartmentManager"` | Dashboard button |
| `Shifts/Index.cshtml` | 68 | `authorize-policy="AdminOnly"` | Settings button |
| `Shifts/NoActiveEvent.cshtml` | 8 | `authorize-policy="AdminOnly"` | "Configure Event Settings" link |
| `ShiftDashboard/Index.cshtml` | 80 | `authorize-policy="ShiftDashboardAccess"` | Volunteer Tracking entry card |
| `ShiftDashboard/Index.cshtml` | 217 | `authorize-policy="ShiftDashboardAccess"` | Volunteer search column |
| `ShiftDashboard/Index.cshtml` | 301, 311 | `authorize-policy="ShiftDashboardAccess"` | Per-row signup-action cells |
| `VolunteerTracking/_VolunteerHeatmap.cshtml` | 5 | `(await AuthService.AuthorizeAsync(User, PolicyNames.VolunteerTrackingWrite)).Succeeded` | Drives `canWrite` flag for cell-level write actions |
| `VolunteerTracking/_VolunteerUnbookedHeatmap.cshtml` | 5 | `(await AuthService.AuthorizeAsync(User, PolicyNames.VolunteerTrackingWrite)).Succeeded` | Drives `canWrite` flag for cell-level write actions |

### Profile Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Profile/Index.cshtml` (`src/Sections/Humans.Users`) | 14 | `authorize-policy="HumanAdminBoardOrAdmin"` | "Admin" link to AdminDetail |
| `Profile/Index.cshtml` | 69 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminBoardOrAdmin)).Succeeded` | `ProfileCardViewMode.Admin` vs `Public` for non-own profiles |
| `Profile/Emails.cshtml` | 15 | `(await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Admin-only email management controls |
| `UsersAdmin/AdminDetail.cshtml` | 9 | `var isAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Drives `isAdmin` flag for Admin-only data blocks |

### Board / Onboarding Review Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Governance/BoardVoting/Detail.cshtml` (`src/Sections/Humans.Governance`) | 101 | `authorize-policy="BoardOnly"` | Vote casting card |

### Team Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Team/Index.cshtml` (`src/Sections/Humans.Teams`) | 18 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminBoardOrAdmin)).Succeeded` | "Summary" + "Sync Status" toolbar buttons on the Teams landing page |
| `Team/Summary.cshtml` | 22 | `authorize-policy="BoardOrAdmin"` | "Create Team" button |
| `Team/Summary.cshtml` | 50 | `authorize-policy="BoardOrAdmin"` | Actions column header |
| `Team/_AdminTeamRow.cshtml` | 44 | `(await AuthService.AuthorizeAsync(User, PolicyNames.BoardOrAdmin)).Succeeded` | Pending-shift-signup badge link |
| `Team/_AdminTeamRow.cshtml` | 96 | `authorize-policy="BoardOrAdmin"` | Actions column cell (Edit/Deactivate buttons) |
| `Team/EditTeam.cshtml` | 81 | `authorize-policy="AdminOnly"` | "Sensitive team" checkbox |
| `Team/Details.cshtml` | 313 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminOrAdmin)).Succeeded` | Drives `canOpenStore` flag (OR'd with coordinator-of-active-top-level-department) for the "Open store" button |
| `Team/Details.cshtml` | 383 | `authorize-policy="@PolicyNames.BoardOrAdmin"` | "Roster" link (matches `TeamAdminController.Roster`'s action-level policy) |

### Camp Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Camp/Index.cshtml` (`src/Sections/Humans.Camps`) | 11 | `authorize-policy="CampAdminOrAdmin"` | "Camp Admin" link |
| `CampAdmin/Index.cshtml` | 460 | `authorize-policy="AdminOnly"` | Danger Zone card (Delete Camp) |

### Ticket Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Ticket/Index.cshtml` (`src/Sections/Humans.Tickets`) | 246 | `authorize-policy="TicketAdminOrAdmin"` | "Sync Now" form |
| `Ticket/Index.cshtml` | 252 | `authorize-policy="AdminOnly"` | "Full Re-sync" form |
| `Ticket/Index.cshtml` | 260 | `authorize-policy="TicketAdminOrAdmin"` | Export link |
| `Ticket/_TicketNav.cshtml` | 26 | `authorize-policy="AdminOnly"` | "Backfill" tab |

### Google Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Google/_SyncTabContent.cshtml` | 8 | `(await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Drives `canExecuteActions` flag for execute-action buttons on the Google sync tab |
| `Google/_SyncTabContent.cshtml` | 9 | `(await AuthService.AuthorizeAsync(User, PolicyNames.BoardOrAdmin)).Succeeded` | Drives `canViewAudit` flag for the audit-log link on the Google sync tab |

### Campaign Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Campaign/Detail.cshtml` (`src/Sections/Humans.Campaigns`) | 23 | `var isAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Drives admin-gated buttons below |
| `Campaign/Detail.cshtml` | 24 | `var canGenerateCodes = (await AuthService.AuthorizeAsync(User, PolicyNames.TicketAdminOrAdmin)).Succeeded` | Drives "Generate Codes" form |

### Admin / Store Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Admin/Index.cshtml` (`src/Humans.Web`) | 14 | `authorize-policy="AdminOnly"` | Open-feedback count in the header stat line |
| `Admin/Index.cshtml` | 125 | `authorize-policy="AdminOnly"` | Recent-activity / dashboard split-panels on the admin landing page |
| `Store/Index.cshtml` (`src/Sections/Humans.Store`) | 10 | `authorize-policy="StoreCatalogAdmin"` | Catalog / Summary / Payments admin button group on the Store landing page |

### Shared Components

| View | Line | Check | Controls |
|---|---|---|---|
| `Shared/Components/ProfileCard/Default.cshtml` (`src/Sections/Humans.Users`) | 28 | `(await AuthService.AuthorizeAsync(User, PolicyNames.HumanAdminBoardOrAdmin)).Succeeded` | Admin / Board view of profile card |
| `Shared/_HumanPopover.cshtml` (`src/Sections/Humans.Users`) | 5 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminBoardOrAdmin)).Succeeded` | Drives `canSeeHiddenTeams` flag (hidden-team list in popover) |
| `Shared/_HumanPopover.cshtml` | 9 | `(await AuthService.AuthorizeAsync(User, PolicyNames.AnyAdminRole)).Succeeded` | Drives `canSeeCamp` flag (camp membership in popover) |
| `Shared/_HumanPopover.cshtml` | 17 | `(await AuthService.AuthorizeAsync(User, PolicyNames.HumanAdminBoardOrAdmin)).Succeeded` | HumanAdmin/Board/Admin popover details (preferred-language flag) |
| `WidgetGallery/Index.cshtml` (`src/Humans.Web`) | 1069 / 1074 | `authorize-policy="@PolicyNames.AdminOnly"` / `authorize-policy="DefinitelyNotARealPolicyName"` | Documentation/demo of the TagHelper (not production gating) |
| `AuthorizeViewTagHelper` (`src/Humans.Base/TagHelpers/AuthorizeViewTagHelper.cs`) | — | `IAuthorizationService.AuthorizeAsync(user, Policy)` | Backs every `authorize-policy="..."` attribute above |
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
| RideshareAdmin or Admin | `[Authorize(Policy = PolicyNames.RideshareAdminOrAdmin)]` | (no current view spelling) |
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
| Resource: IBAN access | No resource handler. `UsersAdminController.RevealIban` is gated by `[Authorize(Policy = PolicyNames.AdminOnly)]` and audits every reveal; expense-report IBAN views render masked with no resource check. `IbanAccessHandler` / `IbanAccessRequirement` were deleted — registered but never constructed, duplicating `AdminOnly`. | (none) |
| Resource: issue handle | `_authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)` | `Model.CanHandle` (view-model) |
| Resource: user-email edit | `_authorizationService.AuthorizeAsync(User, userId, UserEmailOperations.Edit)` | (no view spelling) |
| Resource: agent rate-limit | `auth.AuthorizeAsync(User, user.Id, [new AgentRateLimitRequirement()])` (requirement passed directly — no named `PolicyNames` constant) | (no view spelling) |
| Resource: role assignment | `authorizationService.AuthorizeAsync(User, roleName, PolicyNames.RoleAssignmentManage)` | (UI list driven by `IRoleAssignmentService.GetAssignableRolesAsync`) |

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
| `GoogleController` actions with broader policies (`Sync`, `SyncPreview`, `ProvisionEmail`, `Resource`, `Human`) and `MonitorController.CheckDriveActivity` | TeamsAdmin/Board/Admin / Board/Admin / HumanAdmin/Board/Admin / HumanAdmin/Admin | Each action has its own policy. |
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
| `ExpensesController` | Submitter-side actions (Edit, line CRUD, Submit, Withdraw, Iban) | Inline owner check `report.SubmitterUserId != user.Id → Forbid()` |
| `TeamController` | `EditTeam` (POST) `IsSensitive` flag | `authorizationService.AuthorizeAsync(User, PolicyNames.AdminOnly)` — non-Admin posts leave `IsSensitive` unchanged |
| `StoreController` | Order CRUD/pay | `_authService.AuthorizeAsync(User, order, OrderOperationRequirement.*)` (resource-based) |
| `IssuesController` | All mutating actions | `_authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)` (resource-based) |
| `CityPlanningController` / `CityPlanningApiController` | All actions except `Index`/`GetState` | `RoleChecks.IsCampAdmin(User)` and lead-of-camp checks; three API endpoints also call `_authorizationService.AuthorizeAsync` |
| `GovernanceBoardVotingController` | `Detail` | `RoleChecks.IsAdmin(User)` drives the admin view-model flag (Finalize affordance) — the `Finalize` POST itself is attribute-gated `AdminOnly` |
| `UsersAdminController.AddRole/EndRole` | After `[Authorize(Policy)]` attribute | `authorizationService.AuthorizeAsync(User, roleName, PolicyNames.RoleAssignmentManage)` enforces the role-list filter |
| `ProfileEmailsController` email-edit endpoints | After class-level `[Authorize]` | `_authorizationService.AuthorizeAsync(User, userId, UserEmailOperations.Edit)` (resource-based) |
| `TicketController.Index` | After class-level policy | `RoleChecks.CanAccessFinance(User)` toggles finance-only metrics |
| `MembershipRequiredFilter` | All authenticated requests | Gates the app purely on stored `UserState` (stamped on the principal by `RoleAssignmentClaimsTransformation`): only `Active` reaches the app; `DeletePending` → `/User/Deletion`, Suspended/AdminSuspended/Rejected/Deleted/Merged → `/User/Status`, Bare/unseeded → `/OnboardingWidget`. Roles do not bypass the gate. A Backdoor-API-key-authenticated request (`IsMachineRequest` — `AuthenticationType == BackdoorAuthentication.SchemeName`) passes through unconditionally: a machine principal never runs claims transformation, so the state claim is absent by construction. `Deleted`/`Merged` accounts get no exemptions beyond `Account`, `Language` and `User/Status` — a lingering cookie never reaches a profile. For every other state, exempt controllers (`Account`, `OnboardingWidget`, `Consent`, `User`, `Language`, `Guest`, `GovernanceApplications`, `Issues`, `Notifications`, `Survey`), the own-profile maintenance actions on `Profile` (`Index`, `Me`, `Edit`, `DeclareNotAttending`, `UndoNotAttending`, `MyOutbox`, `Privacy`, `RequestDeletion`, `DietaryMedical`, `CommunicationPreferences`, `UpdatePreference`, `Notifications`, `DownloadData`) and `ProfileEmails` (own email/linked-account grid actions), and `[AllowAnonymous]` pass through; other humans' profiles, messaging, search and admin email actions require `Active`. |
| `NameRequiredFilter` | All requests | Global action filter (registered in `Program.cs` before `MembershipRequiredFilter`). Redirects any authenticated user with no real `BurnerName` to the name form; never blocks sign-in (only redirects). A Backdoor-API-key-authenticated request (`MembershipRequiredFilter.IsMachineRequest`) passes through unconditionally — a JSON client cannot fill in the name form. `Deleted`/`Merged` accounts (names cleared by anonymization) pass through to `MembershipRequiredFilter`'s status wall. Exempt controllers (`Account`, `Language`), exempt actions (`OnboardingWidget/Names`, `Home/Error`, `Home/Privacy`), and `[AllowAnonymous]` pass through. |
| `HangfireAuthorizationFilter` | Hangfire dashboard | `RoleChecks.IsAdmin(User)` |
| `AgentController.Ask` | Per-request | `auth.AuthorizeAsync(User, user.Id, [new AgentRateLimitRequirement()])` (resource-based; requirement instantiated directly rather than via a named policy) |
| `GateController.Decision` | Supervisor overrides (too-early / unconfirmed-EE admit, child-without-ID waiver) | Shared override PIN from `Gate:SupervisorPin` config, verified server-side (`SupervisorPinValid` — SHA-256 fixed-time compare) and brute-force-throttled via `GatePinThrottle` (one shared bucket for the terminal: 5 tries / 15 min); fail-closed when no PIN is configured. The PIN authorizes but cannot attribute — the event records the gate account. |
| `GateController.Claim` (POST) / `ClaimPin` | Claimant identity | Claimant must be a real active member (`UserService.GetUserInfoAsync(...).IsActive` — guards a direct POST with an arbitrary/inactive id); `ClaimPin` verifies (`IGateService.VerifyPinAsync`) or enrols (`SetOwnPinAsync`) the staffer's personal PIN with per-target-user `GatePinThrottle` buckets, then stamps the verified user id into the session server-side (attribution can't be forged from the request body) |

---

## 5. Canonical Policy Name Table

Policy *names* are shared vocabulary in `PolicyNames` — nav items and cross-section checks cite other sections' policies by name. Registration is split ([`section-contribution-seams`](../memory/architecture/section-contribution-seams.md)): the cross-section policies `AdminOnly`, `AnyAdminRole`, `BoardOnly`, `BoardOrAdmin`, `ScannerAccess`, `GateAdmit`, `ReviewQueueAccess`, `AppAccess` and `RoleAssignmentManage` are registered in Shell's `AuthorizationPolicyExtensions.AddHumansAuthorizationPolicies`; every other policy is registered by its owning section's `SectionPolicies : ISectionPolicies`, discovered by `SectionDiscoveryExtensions.DiscoverImplementations<ISectionPolicies>()` and applied additively through `services.Configure<AuthorizationOptions>` in the same method.

| Owning section | Policies registered in its `SectionPolicies.cs` |
|---|---|
| Camps | `CampAdminOrAdmin`, `CampComplianceAccess` |
| Cantina | `CantinaAdminOrAdmin` |
| CityPlanning | `CityPlanningMapAdmin` |
| Consent | `ConsentCoordinatorBoardOrAdmin` |
| Events | `EventsAdminOrAdmin` |
| Finance | `FinanceAdminOrAdmin` |
| Rideshare | `RideshareAdminOrAdmin` |
| Shifts | `ShiftDashboardAccess`, `ShiftDepartmentManager`, `VolunteerTrackingWrite`, `PrivilegedSignupApprover`, `VolunteerManager`, `MedicalDataViewer` |
| Store | `StoreCatalogAdmin` |
| Teams | `TeamsAdminBoardOrAdmin`, `TeamsAdminOrAdmin` |
| Tickets | `TicketAdminBoardOrAdmin`, `TicketAdminOrAdmin` |
| Users | `HumanAdminBoardOrAdmin`, `HumanAdminOrAdmin`, `HumanAdminOnly` |

Each policy maps from the current authorization dialect(s) to a single canonical name.

| Canonical Policy Name | Roles | Current Sources |
|---|---|---|
| `AdminOnly` | Admin | `PolicyNames.AdminOnly`, `RoleChecks.IsAdmin` |
| `AnyAdminRole` | Admin, Board, HumanAdmin, TeamsAdmin, CampAdmin, TicketAdmin, EventsAdmin, FeedbackAdmin, FinanceAdmin, StoreAdmin, CantinaAdmin, RideshareAdmin, NoInfoAdmin, VolunteerCoordinator, ConsentCoordinator | `PolicyNames.AnyAdminRole` (admin-shell entry-point gate) |
| `BoardOnly` | Board | `PolicyNames.BoardOnly` |
| `BoardOrAdmin` | Board, Admin | `PolicyNames.BoardOrAdmin`, `RoleChecks.IsAdminOrBoard` |
| `HumanAdminBoardOrAdmin` | HumanAdmin, Board, Admin | `PolicyNames.HumanAdminBoardOrAdmin`, `RoleChecks.IsHumanAdminBoardOrAdmin` |
| `HumanAdminOrAdmin` | HumanAdmin, Admin | `PolicyNames.HumanAdminOrAdmin` |
| `TeamsAdminBoardOrAdmin` | TeamsAdmin, Board, Admin | `PolicyNames.TeamsAdminBoardOrAdmin`, `RoleChecks.IsTeamsAdminBoardOrAdmin` |
| `TeamsAdminOrAdmin` | TeamsAdmin, Admin | `PolicyNames.TeamsAdminOrAdmin` (registered but currently only referenced from `Team/Details.cshtml`'s "Open store" boolean — no controller attribute uses it yet) |
| `CampAdminOrAdmin` | CampAdmin, Admin | `PolicyNames.CampAdminOrAdmin`, `RoleChecks.IsCampAdmin` |
| `CampComplianceAccess` | CampAdmin, Admin OR any team/sub-team coordinator | `PolicyNames.CampComplianceAccess` (composite — `CampComplianceAccessHandler`) |
| `CityPlanningMapAdmin` | CampAdmin, Admin OR any city-planning team member | `PolicyNames.CityPlanningMapAdmin` (composite — `CityPlanningMapAdminHandler`; gates the `/Settings#city-planning` tab) |
| `TicketAdminBoardOrAdmin` | TicketAdmin, Admin, Board | `PolicyNames.TicketAdminBoardOrAdmin`, `RoleChecks.CanAccessTickets` |
| `TicketAdminOrAdmin` | TicketAdmin, Admin | `PolicyNames.TicketAdminOrAdmin`, `RoleChecks.CanManageTickets` |
| `ScannerAccess` | TicketAdmin, Admin, Board OR `SystemUserIds.GateTerminal` (by NameIdentifier claim) | `PolicyNames.ScannerAccess` (composite assertion — gate-terminal account admitted by id, not by role) |
| `GateAdmit` | TicketAdmin, Admin, Board OR `SystemUserIds.GateTerminal` (by NameIdentifier claim) | `PolicyNames.GateAdmit` (composite assertion — gate write actions; same principals as `ScannerAccess` today, kept separate so the write path never rides the read-only Scanner gate and the two can diverge) |
| `FinanceAdminOrAdmin` | FinanceAdmin, Admin | `PolicyNames.FinanceAdminOrAdmin`, `RoleChecks.IsFinanceAdmin`, `RoleChecks.CanAccessFinance` |
| `EventsAdminOrAdmin` | EventsAdmin, Admin | `PolicyNames.EventsAdminOrAdmin` |
| `CantinaAdminOrAdmin` | CantinaAdmin, Admin | `PolicyNames.CantinaAdminOrAdmin` (Cantina coordinator surface) |
| `RideshareAdminOrAdmin` | RideshareAdmin, Admin | `PolicyNames.RideshareAdminOrAdmin` (Rideshare admin surface: settings, stats, day roster) |
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
| `RoleAssignmentManage` | (resource-based — the resource is the target role-name string) | `PolicyNames.RoleAssignmentManage` (wraps `RoleAssignmentOperationRequirement.Manage`, owned by `Humans.Auth` — `UsersAdminController.AddRole`/`EndRole` call `AuthorizeAsync(User, roleName, PolicyNames.RoleAssignmentManage)` instead of passing the requirement object directly — see §6) |

### Notes on Policy Design

- `ShiftDashboardAccess` and `ShiftDepartmentManager` are intentionally distinct: dashboard access is role-list-based, department manager additionally permits any team manager/coordinator (composite via `IsAnyTeamManagerOrCoordinatorHandler`).
- `AppAccess` is the single nav-visibility gate: `UserState == Active` (the user entered their legal name). A plain `RequireAssertion` — no custom requirement/handler. It replaced the former `IsActiveMember` / `ActiveMemberOrShiftAccess` policies (and there is no separate shift access).
- `CampComplianceAccess` is deliberately broader than `CampAdminOrAdmin`: it short-circuits for CampAdmin/Admin and otherwise admits any team/sub-team coordinator (composite via `CampComplianceAccessHandler`, reusing the same `IShiftManagementService.GetCoordinatorTeamIdsAsync` lookup as `IsAnyTeamManagerOrCoordinatorHandler`). It gates only the read-only Barrios compliance matrix; the camp-management surface in `CampAdminController` stays CampAdmin-only.
- `HumanAdminOnly` is a composite policy used for the nav "Humans" link that only shows when the user has HumanAdmin but not the broader Board/Admin access.
- `MedicalDataViewer` is a data-access policy, not a page-access policy. It controls whether medical fields are visible within pages the user already has access to.
- `GateAdmit` is deliberately a twin of `ScannerAccess` (same assertion body): the durable gate write surface (`/Gate/Decision`, `/Gate/Claim` POST, `/Gate/ClaimPin`, `/Gate/EndShift`) must never inherit a future loosening of the read-only Scanner gate. Supervisor overrides inside `Decision` are a second factor on top of the policy — the shared `Gate:SupervisorPin`, not an identity check (see §4).
- `AnyAdminRole` gates the admin-shell entry point (`/Admin`). Sidebar items inside the shell are filtered per-item by `AdminSidebarViewComponent` against each item's policy. The role list mirrors the top-nav check in `_Layout.cshtml` and includes the grantable `CantinaAdmin` and `RideshareAdmin` roles added with their sections' admin surfaces.
- Object-relative policies (coordinator of specific team, camp lead of specific camp, camp-event submitter, budget category for coordinator's department, manageable role for HumanAdmin, expense reports, store orders, containers, issues, user-email edits, agent rate-limit) are implemented as resource-based authorization handlers — see §6.

---

## 6. Resource-Based Authorization Handlers

Resource-based authorization handlers are subclasses of `AuthorizationHandler<TRequirement, TResource>` (or `AuthorizationHandler<TRequirement>` / `IAuthorizationHandler` directly when the same handler covers multiple resource shapes) that evaluate whether a user can perform an operation on a specific resource instance. They are invoked via `IAuthorizationService.AuthorizeAsync(User, resource, requirement)` from controllers (or controller base classes).

Every handler — resource-based and composite — lives in its owning section's `Authorization/` folder and is listed in that section's `authorization.md` (see Per-Section Inventories above). The composite (non-resource) handlers backing named policies:

| Handler | Requirement | Owning section | Backs policy |
|---|---|---|---|
| `HumanAdminOnlyHandler` | `HumanAdminOnlyRequirement` | Users | `HumanAdminOnly` |
| `IsAnyTeamManagerOrCoordinatorHandler` | `IsAnyTeamManagerOrCoordinatorRequirement` | Shifts | `ShiftDepartmentManager` |
| `CampComplianceAccessHandler` | `CampComplianceAccessRequirement` | Camps | `CampComplianceAccess` |
| `CityPlanningMapAdminHandler` | `CityPlanningMapAdminRequirement` | CityPlanning | `CityPlanningMapAdmin` |

The only authorization files left in `src/Humans.Web/` are `Authorization/AuthorizationPolicyExtensions.cs`, `MembershipRequiredFilter.cs`, `NameRequiredFilter.cs`, the claims/identity plumbing (`HttpCurrentUserContext.cs`, `HumansUserClaimsPrincipalFactory.cs`, `RoleAssignmentClaimsTransformation.cs`) and `HangfireAuthorizationFilter.cs` at the project root. The framework-facing plumbing (`PolicyNames`, `RoleNames`, `RoleGroups`, `RoleChecks`, `ShiftRoleChecks`, `HumansControllerBase`, `AuthorizeViewTagHelper`) lives in `src/Humans.Base/` under the namespaces `Humans.Base.Authorization` / `Humans.Base.Constants` / `Humans.Base.Controllers` / `Humans.Base.TagHelpers`.

### `IAuthorizationService.AuthorizeAsync` Call Sites

| File | Line | Call |
|---|---|---|
| `src/Sections/Humans.Teams/Contracts/HumansTeamControllerBase.cs` | 28 | `AuthorizeAsync(User, team, TeamOperationRequirement.ManageCoordinators)` (`ResolveTeamManagementAsync`) |
| `src/Sections/Humans.Teams/Contracts/HumansTeamControllerBase.cs` | 41 | `AuthorizeAsync(User, team, TeamOperationRequirement.ManageEarlyEntry)` (`ResolveEarlyEntryManagementAsync`) |
| `src/Sections/Humans.Teams/Controllers/TeamController.cs` | 162 | `AuthorizeAsync(User, teamInfo, TeamOperationRequirement.ManageEarlyEntry)` (drives `CanManageEarlyEntry` view-model flag on team details) |
| `src/Sections/Humans.Teams/Controllers/TeamController.cs` | 681 | `AuthorizeAsync(User, PolicyNames.AdminOnly)` (EditTeam POST — `IsSensitive` leave-unchanged guard for non-Admin editors) |
| `src/Sections/Humans.Camps/Contracts/HumansCampControllerBase.cs` | 25 | `AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` (non-public season visibility) |
| `src/Sections/Humans.Camps/Contracts/HumansCampControllerBase.cs` | 53 | `AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` |
| `src/Sections/Humans.Camps/Contracts/HumansCampControllerBase.cs` | 83 | `AuthorizeAsync(User, camp, CampOperationRequirement.SubmitEvent)` |
| `src/Sections/Humans.Budget/Controllers/BudgetController.cs` | 29, 112 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` |
| `src/Sections/Humans.Budget/Controllers/BudgetController.cs` | 92 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` (drives `IsCoordinator` flag alongside a coordinator-team-id lookup) |
| `src/Sections/Humans.Budget/Controllers/BudgetController.cs` | 118, 229 | `AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)` |
| `src/Sections/Humans.Containers/Controllers/ContainerController.cs` | 25 | `AuthorizeAsync(User, target, requirement)` (private helper, called from every mutating action in the controller) |
| `src/Sections/Humans.Expenses/Controllers/ExpensesController.cs` | 172, 651, 682, 708, 769, 797, 824 | `AuthorizeAsync(User, report, new ExpenseReportOperationRequirement(ExpenseReportOperation.X))` — `View` (Detail 172, Attachment 651), `Endorse` 682, `CoordinatorReject` 708, `Approve` 769, `FinanceReject` 797, `RequeueHoldedPush` 824 (backs `HoldedRetry`) |
| `src/Sections/Humans.Expenses/Controllers/ExpensesController.cs` | 878 | `AuthorizeAsync(User, report, new ExpenseReportOperationRequirement(operation))` (private `AllowsAsync` — drives the Detail page's action affordances) |
| `src/Sections/Humans.Expenses/Controllers/ExpensesController.cs` | 191, 736, 886 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` (Detail finance flag, Review queue scope, `IsFinanceAdminAsync` for the New-report member picker) |
| `src/Sections/Humans.Store/Controllers/StoreController.cs` | 55, 77, 80, 81, 82, 83, 144, 184, 267, 297, 324, 349 | `AuthorizeAsync(User, order/resource, OrderOperationRequirement.X)` — line-deadline-aware `AddLine`/`RemoveLine` at 297/324 authorize against an `OrderLineContext` |
| `src/Sections/Humans.Store/Controllers/StoreController.cs` | 109, 126 | `AuthorizeAsync(User, new OrderLineContext(...), OrderOperationRequirement.{AddLine, RemoveLine})` (private `FilterLineEditAffordancesAsync` helper — drives the per-product/per-line "can I still edit" affordances shown on the order page) |
| `src/Sections/Humans.Store/Controllers/StoreController.cs` | 220, 238 | `AuthorizeAsync(User, new OrderCreateContext(...), OrderOperationRequirement.Create)` (camp order `Create` at 220, team order `CreateTeamOrder` at 238) |
| `src/Sections/Humans.Issues/Controllers/IssuesController.cs` | 199, 276, 323, 351, 379, 411 | `AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)` |
| `src/Sections/Humans.CityPlanning/Controllers/CityPlanningApiController.cs` | 272, 297, 335 | `AuthorizeAsync(User, ContainerAuthorizationTarget.For(container), ContainerOperationRequirement.Place)` (container placement save, notes, clear) |
| `src/Sections/Humans.Surveys/Controllers/SurveyAdminController.cs` | 123, 179, 185, 195, 220, 252, 281, 314, 359, 601, 639 | `AuthorizeAsync(User, detail, new SurveyOperationRequirement(SurveyOperation.X))` — `Submit` (123, 185, 639), `Edit` (179, 359), `Preview` (195, 220, 252, 281, 314), `ViewResults` (601) |
| `src/Sections/Humans.Workgroups/Controllers/WorkgroupsController.cs` | 397, 400 | `AuthorizeAsync(User, workgroup, WorkgroupOperationRequirement.{Administer, Member})` (page permissions) |
| `src/Sections/Humans.Users/Controllers/ProfileEmailsController.cs` | 209, 242, 287, 329, 366, 403, 440, 460, 504, 583, 607, 633, 666, 692, 718, 893, 919, 963 | `AuthorizeAsync(User, userId, UserEmailOperations.Edit)` (the email-edit endpoints) |
| `src/Sections/Humans.Users/Controllers/ProfileViewController.cs` | 111 | `AuthorizeAsync(User, PolicyNames.TicketAdminBoardOrAdmin)` (onsite-chip visibility gate) |
| `src/Sections/Humans.Users/Controllers/ProfileViewController.cs` | 213 | `AuthorizeAsync(User, PolicyNames.PrivilegedSignupApprover)` (drives `isPrivilegedApprover` — gates whether a non-own-profile viewer sees the "sent messages" panel on the profile page; admits coordinators or `PrivilegedSignupApprover` role) |
| `src/Sections/Humans.Users/Controllers/UsersAdminController.cs` | 293 | `AuthorizeAsync(User, model.RoleName, PolicyNames.RoleAssignmentManage)` (AddRole — goes through the named policy rather than passing `RoleAssignmentOperationRequirement.Manage` directly; see §5) |
| `src/Sections/Humans.Users/Controllers/UsersAdminController.cs` | 331 | `AuthorizeAsync(User, roleAssignment.RoleName, PolicyNames.RoleAssignmentManage)` (EndRole) |
| `src/Sections/Humans.Agent/Controllers/AgentController.cs` | 60 | `AuthorizeAsync(User, user.Id, [new AgentRateLimitRequirement()])` (requirement instantiated directly, no `PolicyNames` constant) |
| `src/Sections/Humans.Settings/ViewComponents/SettingsTabComposition.cs` | 30 | `AuthorizeAsync(user, null, tab.Policy)` (filters the `/Settings` tabs sections contribute) |
| `src/Sections/Humans.Settings/ViewComponents/EventSettingsTabViewComponent.cs` | 24 | `AuthorizeAsync(UserClaimsPrincipal, null, PolicyNames.AdminOnly)` (Event tab editable vs read-only) |
| `src/Humans.Base/TagHelpers/AuthorizeViewTagHelper.cs` | 54 | `AuthorizeAsync(user, Policy)` (driver of `<authorize-policy>` view tags) |
| `src/Humans.Web/ViewComponents/AdminSidebarViewComponent.cs` | 31 | `AuthorizeAsync(HttpContext.User, null, item.Policy)` (filters admin sidebar) |
| `src/Humans.Web/ViewComponents/AdminSummaryViewComponent.cs` | 34 | `AuthorizeAsync(HttpContext.User, null, tile.Policy)` (filters admin dashboard tiles) |
| `src/Humans.Web/ViewComponents/SectionNavViewComponent.cs` | 64 | `AuthorizeAsync(HttpContext.User, null, item.Policy)` (filters section nav items) |

---

## 7. Notes / Known Deviations

- **No `[Authorize(Roles = ...)]` attributes remain anywhere in `src/`** — every controller/action `[Authorize]` attribute now references a `PolicyNames` constant or is a bare authenticated/`[AllowAnonymous]` marker.
- **`ScannerController` uses `PolicyNames.ScannerAccess`**, not `TicketAdminBoardOrAdmin` — the `ScannerAccess` policy is a composite assertion that additionally admits the shared gate-terminal account by its well-known `SystemUserIds.GateTerminal` NameIdentifier claim so the kiosk session can scan without holding any role.
- **The Gate section (`/Gate`) splits read from write**: `GateController` reads under `ScannerAccess`, but every state-changing action (`Decision`, `Claim` POST, `ClaimPin`, `EndShift`) is gated by the separate `GateAdmit` policy. Scan attribution is session-based, stamped server-side after an active-member + personal-PIN check; supervisor overrides use the shared `Gate:SupervisorPin` config value (server-verified, throttled, fail-closed) — the PIN authorizes but never attributes. `GateController.Search` is a deliberately name-only, masked-email people search so the route-locked kiosk never exposes the broader `/api/profiles/search` surface.
- **`SurveyController` is `[AllowAnonymous]`** — the entire public survey wizard is unauthenticated; identity flows from the invitation token, not the principal. `SurveyAdminController` (`AppAccess` plus per-survey `SurveyOperationRequirement`, `BoardOrAdmin` on the run/approval actions) and, in the Backdoor section, `BackdoorSurveysController` (`BackdoorApiKeyAuthFilter`) are the gated surfaces.
- **`ICalFeedApiController` is `[AllowAnonymous]`** — the personal iCal feed uses a secret token in the URL for authentication; all failure modes return 404 to prevent oracle attacks.
- The Events Guide controllers and `_Layout.cshtml` Events sub-dropdowns have all migrated to `PolicyNames.EventsAdminOrAdmin`.

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
| `RideshareAdmin` | `"RideshareAdmin"` |

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
