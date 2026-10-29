# Authorization Inventory

**Phase 1 is complete:** every canonical policy in §5 is registered in `AuthorizationPolicyExtensions.AddHumansAuthorizationPolicies`, all controllers use `[Authorize(Policy = PolicyNames.X)]`, the `authorize-policy` TagHelper resolves through `IAuthorizationService`, and no view calls `RoleChecks.*` / `ShiftsRoles.*` directly (§2). **Phase 2 (resource-based authorization)** has shipped multiple vertical slices — see §6. **Phase 3 (service-layer enforcement) is cancelled.**

Covers every `[Authorize(Policy)]` / `[Authorize(Roles)]` attribute on controllers and actions across `src/Humans.Web/Controllers/` and every section project under `src/Sections/*/Controllers/`, every `RoleChecks.*` / `ShiftsRoles.*` invocation across `src/Humans.Web/`, `src/Sections/**`, and `src/Humans.Base/`, every `IAuthorizationService.AuthorizeAsync` call site, every `authorize-policy` TagHelper attribute (implemented by `AuthorizeViewTagHelper` in `src/Humans.Base/TagHelpers/`) and `User.IsInRole` / `Model.X` authorization check across views and view components, and every `AuthorizationHandler<T, R>` (and `IAuthorizationHandler`) under `src/Humans.Web/Authorization/`, `src/Humans.Base/Authorization/`, and `src/Sections/**/Authorization/`. (`Humans.Domain`, `Humans.Application`, and `Humans.UI` no longer exist; their non-Razor plumbing lives in `Humans.Base`, under `Humans.Base.*` namespaces — see §6.)

The `Source` column reflects the constant referenced in the attribute as it appears in the code today.

**Authenticated by default.** `AddHumansAuthorizationPolicies` sets a `FallbackPolicy` of `RequireAuthenticatedUser`, so every endpoint without its own authorization metadata (health checks, hubs, minimal APIs, Hangfire) needs a signed-in user, and `HumansControllerBase` / `ApiControllerBase` carry `[Authorize]`, which every descendant inherits. A public surface says so with `[AllowAnonymous]` or `.AllowAnonymous()`; `EndpointAuthorizationTests` pins the full list of anonymous controllers and actions. A row below reading "no class-level `[Authorize]`" therefore means authenticated-by-default, not open.

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
| `AdminController.Index` | Action | `Admin, Board, UsersAdmin, TeamsAdmin, CampsAdmin, TicketsAdmin, EventsAdmin, FeedbackAdmin, FinanceAdmin, StoreAdmin, CantinaAdmin, RideshareAdmin, ShiftsNoInfo, ShiftsAdmin, ConsentCoordinator` | `PolicyNames.AnyAdminRole` (the only action left on the gutted dashboard controller) |

`/Admin/*` is a nav holder, not a section — each admin surface's controller lives in the section it acts on (see Per-Section Inventories above). The admin shell's sidebar groups and tabs come from sections' `ISectionAdminNav` contributions, each item filtered by its own policy (`AdminNavItems`, see §2 Shared Components); only the dashboard link is `AnyAdminRole`-gated.

### About / Home / Account / Misc

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `AboutController` (`src/Humans.Web`) | Class | `[Authorize]` (inherited from `HumansControllerBase`) | — |
| `AboutController.Index` | Action | `AllowAnonymous` | Override |
| `AboutController.Staff` | Action | `[Authorize]` (authenticated) | — |
| `HomeController` (`src/Humans.Web`) | Class | `[Authorize]` (inherited from `HumansControllerBase`) | — |
| `HomeController.Index` / `Privacy` / `Error` | Action | `AllowAnonymous` | Override. `Index` is the landing page and the dashboard on one route: `MembershipRequiredFilter.IsPublicEndpoint` keeps the state and name gates on it for signed-in users |
| `AccountController` (`src/Humans.Web`) | Class | `AllowAnonymous` | Sign-in flows run before there is a principal |
| `AccountController.GateLogin` (GET/POST) | Action | (anonymous via the class) | — (shared kiosk credential login at `/Account/GateLogin`; IP-throttled via `GateLoginThrottle`; never gated by role — the gate-terminal account holds no roles) |
| `LanguageController` (`src/Humans.Web`) | Class | `AllowAnonymous` | Culture switch on the login page |

`GuestController` lives in `Humans.Onboarding`; `ColorPaletteController` and `WidgetGalleryController` (`/Debug/WidgetGallery`) in `Humans.Debug`; `TicketsGateAdminController` in `Humans.Tickets` — see those sections' authorization docs.

### Public / API

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `TimezoneApiController` (`src/Humans.Web`) | Class | `AllowAnonymous` | Browser beacon |
| `/health`, `/health/live`, `/health/ready`, `/metrics` (`Program.cs`) | Endpoint | `.AllowAnonymous()` | Probed and scraped by infrastructure that holds no cookie |
| `HangfireAuthorizationFilter` (`src/Humans.Web`) | Filter | `RoleChecks.IsAdminOrBoard(User)` | Admin and Board open the dashboard; `DashboardOptions.IsReadOnlyFunc` makes it read-only for everyone but Admin outside Development (the fallback policy already requires a signed-in user before the filter runs) |

---

## 2. View Authorization Map

Views express authorization four ways today:

1. **`authorize-policy="PolicyName"` TagHelper attribute** — the dominant pattern. Resolves through `IAuthorizationService.AuthorizeAsync(User, policyName)` via `AuthorizeViewTagHelper`. Hides the element when the policy fails.
2. **`(await AuthService.AuthorizeAsync(User, PolicyNames.X)).Succeeded`** — used when a view needs the boolean for branching, multi-use within the page, or to drive a `var` flag rather than gate one element. Requires `@inject IAuthorizationService AuthService`.
3. **`User.IsInRole(RoleNames.X)` direct calls** — not present in any view file.
4. **`Model.CanX` / `Model.IsX` view-model properties** — for resource-relative checks (coordinator-of-this-team, lead-of-this-camp, can-edit-this-budget) and for status-driven UI (suspended badge, approved badge, etc.). The view does not know about roles; the controller / view-model author resolved authorization upstream.

`RoleChecks.*` / `ShiftsRoles.*` are invoked from no view.

### Nav Layout (`_Layout.cshtml`)

| Line | Check | Controls |
|---|---|---|
| 67 | `var isFullAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Drives `isFullAdmin` flag for build-hash tooltip on brand link (commit SHA on hover) — gated to FullAdmin (`AdminOnly`), not `AnyAdminRole` |
| 121 | `authorize-policy="AnyAdminRole"` | Admin nav link (entry to admin shell) |

Every other top-nav link is contributed by its section through `ISectionNav` and rendered by `<vc:section-nav />` (`SectionNavViewComponent`), which filters each item by its own policy — see Shared Components.

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
| `Shifts/Index.cshtml` (`src/Sections/Humans.Shifts`) | 84 | `authorize-policy="ShiftDepartmentManager"` | Dashboard button |
| `Shifts/Index.cshtml` | 85 | `authorize-policy="AdminOnly"` | Settings button |
| `Shifts/NoActiveEvent.cshtml` | 8 | `authorize-policy="AdminOnly"` | "Configure Event Settings" link |
| `ShiftDashboard/Index.cshtml` | 73 | `authorize-policy="ShiftDashboardAccess"` | Volunteer Tracking entry card |
| `ShiftDashboard/Index.cshtml` | 210 | `authorize-policy="ShiftDashboardAccess"` | Volunteer search column |
| `ShiftDashboard/Index.cshtml` | 292, 302 | `authorize-policy="ShiftDashboardAccess"` | Per-row signup-action cells |
| `VolunteerTracking/_VolunteerHeatmap.cshtml` | 5 | `(await AuthService.AuthorizeAsync(User, PolicyNames.VolunteerTrackingWrite)).Succeeded` | Drives `canWrite` flag for cell-level write actions |
| `VolunteerTracking/_VolunteerUnbookedHeatmap.cshtml` | 5 | `(await AuthService.AuthorizeAsync(User, PolicyNames.VolunteerTrackingWrite)).Succeeded` | Drives `canWrite` flag for cell-level write actions |

### Profile Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Profile/Index.cshtml` (`src/Sections/Humans.Users`) | 14 | `authorize-policy="HumanAdminBoardOrAdmin"` | "Admin" link to AdminDetail |
| `Profile/Index.cshtml` | 61 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminBoardOrAdmin)).Succeeded` | `ProfileCardViewMode.Admin` vs `Public` for non-own profiles |
| `ProfileEmails/Emails.cshtml` | 14 | `(await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Admin-only email management controls |
| `UsersAdmin/AdminDetail.cshtml` | 5 | `var isAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Drives `isAdmin` flag for Admin-only data blocks |

### Governance Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Governance/BoardVoting/Detail.cshtml` (`src/Sections/Humans.Governance`) | 96 | `authorize-policy="BoardOnly"` | Vote casting card |
| `Governance/Votes/Admin/Index.cshtml` | 62, 70, 91, 106 | `authorize-policy="AdminOnly"` | Open / extend / cancel controls on assembly votes |

### Team Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Team/Index.cshtml` (`src/Sections/Humans.Teams`) | 19 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminBoardOrAdmin)).Succeeded` | "Summary" + "Sync Status" toolbar buttons on the Teams landing page |
| `Team/Summary.cshtml` | 22 | `authorize-policy="TeamsAdminBoardOrAdmin"` | "Create Team" button (matches `TeamController.CreateTeam`) |
| `Team/Summary.cshtml` | 50 | `authorize-policy="TeamsAdminBoardOrAdmin"` | Actions column header |
| `Team/_AdminTeamRow.cshtml` | 42 | `(await AuthService.AuthorizeAsync(User, PolicyNames.BoardOrAdmin)).Succeeded` | Pending-shift-signup badge link |
| `Team/_AdminTeamRow.cshtml` | 94 | `authorize-policy="TeamsAdminBoardOrAdmin"` | Actions column cell (Edit button) |
| `Team/_AdminTeamRow.cshtml` | 101 | `authorize-policy="BoardOrAdmin"` | Deactivate form (matches `TeamController.DeleteTeam`) |
| `Team/EditTeam.cshtml` | 81 | `authorize-policy="AdminOnly"` | "Sensitive team" checkbox |
| `Team/Details.cshtml` | 316 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminOrAdmin)).Succeeded` | Drives `canOpenStore` flag (OR'd with coordinator-of-active-top-level-department) for the "Open store" button |
| `Team/Details.cshtml` | 386 | `authorize-policy="@PolicyNames.BoardOrAdmin"` | "Roster" link (matches `TeamAdminController.Roster`'s action-level policy) |

### Camp Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Camp/Index.cshtml` (`src/Sections/Humans.Camps`) | 11 | `authorize-policy="CampAdminOrAdmin"` | "Camps Admin" link |
| `Camp/Index.cshtml` | 24 | `authorize-policy="AppAccess"` | "Register a barrio" button |
| `CampAdmin/Index.cshtml` | 386 | `authorize-policy="AdminOnly"` | Danger Zone card (Delete Camp) |

### Ticket Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Ticket/Index.cshtml` (`src/Sections/Humans.Tickets`) | 254 | `authorize-policy="TicketAdminOrAdmin"` | "Sync Now" form |
| `Ticket/Index.cshtml` | 260 | `authorize-policy="AdminOnly"` | "Full Re-sync" form |
| `Ticket/Index.cshtml` | 268 | `authorize-policy="TicketAdminOrAdmin"` | Export link |
| `Ticket/SalesAggregates.cshtml` | 83 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TicketAdminOrAdmin)).Succeeded` | TicketsAdmin export control |
| `Ticket/SalesAggregates.cshtml` | 87 | `(await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Admin-only donations export (`ExportDonations`) |
| `Ticket/_TicketNav.cshtml` | 23 | `authorize-policy="AdminOnly"` | "Backfill" tab |

### Google Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Google/_SyncTabContent.cshtml` | 8 | `(await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Drives `canExecuteActions` flag for execute-action buttons on the Google sync tab |
| `Google/_SyncTabContent.cshtml` | 9 | `(await AuthService.AuthorizeAsync(User, PolicyNames.BoardOrAdmin)).Succeeded` | Drives `canViewAudit` flag for the audit-log link on the Google sync tab |

### Campaign Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Campaign/Detail.cshtml` (`src/Sections/Humans.Campaigns`) | 15 | `var isAdmin = (await AuthService.AuthorizeAsync(User, PolicyNames.AdminOnly)).Succeeded` | Drives admin-gated buttons below |
| `Campaign/Detail.cshtml` | 16 | `var canGenerateCodes = (await AuthService.AuthorizeAsync(User, PolicyNames.TicketAdminOrAdmin)).Succeeded` | Drives "Generate Codes" form |

### Admin / Store / Finance Views

| View | Line | Check | Controls |
|---|---|---|---|
| `Shared/Components/AdminActivityCard/Default.cshtml` (`src/Sections/Humans.AuditLog`) | 1 | `authorize-policy="AdminOnly"` | Recent-activity split-panels on the admin landing page |
| `Store/Index.cshtml` (`src/Sections/Humans.Store`) | 10 | `authorize-policy="StoreCatalogAdmin"` | Catalog / Summary / Payments admin button group on the Store landing page |
| `Expenses/New.cshtml` (`src/Sections/Humans.Expenses`) | 27 | `authorize-policy="FinanceAdminOrAdmin"` | "For member" submitter picker |
| `Rideshare/Mine.cshtml` (`src/Sections/Humans.Rideshare`) | 7 | `(await AuthService.AuthorizeAsync(User, PolicyNames.RideshareAdminOrAdmin)).Succeeded` | Drives `canAdminister` flag |
| `SurveyAdmin/Results.cshtml` / `Builder.cshtml` (`src/Sections/Humans.Surveys`) | 17, 19, 243 / 24 | `authorize-policy="@...PolicyNames.BoardOrAdmin"` | Admin breadcrumbs and Board/Admin-only results controls |

### Shared Components

| View | Line | Check | Controls |
|---|---|---|---|
| `Shared/Components/ProfileCard/Default.cshtml` (`src/Sections/Humans.Users`) | 28 | `(await AuthService.AuthorizeAsync(User, PolicyNames.HumanAdminBoardOrAdmin)).Succeeded` | Admin / Board view of profile card |
| `Shared/_HumanPopover.cshtml` (`src/Sections/Humans.Users`) | 5 | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminBoardOrAdmin)).Succeeded` | Drives `canSeeHiddenTeams` flag (hidden-team list in popover) |
| `Shared/_HumanPopover.cshtml` | 9 | `(await AuthService.AuthorizeAsync(User, PolicyNames.AnyAdminRole)).Succeeded` | Drives `canSeeCamp` flag (camp membership in popover) |
| `Shared/_HumanPopover.cshtml` | 17 | `(await AuthService.AuthorizeAsync(User, PolicyNames.HumanAdminBoardOrAdmin)).Succeeded` | UsersAdmin/Board/Admin popover details (preferred-language flag) |
| `WidgetGallery/Index.cshtml` (`src/Sections/Humans.Debug`) | 1078 / 1083 | `authorize-policy="@PolicyNames.AdminOnly"` / `authorize-policy="DefinitelyNotARealPolicyName"` | Documentation/demo of the TagHelper (not production gating) |
| `AuthorizeViewTagHelper` (`src/Humans.Base/TagHelpers/AuthorizeViewTagHelper.cs`) | — | `IAuthorizationService.AuthorizeAsync(user, Policy)` | Backs every `authorize-policy="..."` attribute above |
| `AdminNavItems` (`src/Humans.Web/ViewComponents/AdminNavItems.cs`) | 69 | `IAuthorizationService.AuthorizeAsync(user, null, item.Policy)` (else the item's `RoleCheck`) | Filters the admin sidebar groups and tabs per item |
| `AdminSidebarViewComponent` | 26 | `IAuthorizationService.AuthorizeAsync(HttpContext.User, PolicyNames.AnyAdminRole)` | Dashboard link in the admin sidebar |
| `SectionNavViewComponent` (`src/Humans.Base/ViewComponents/`) | 64 | `IAuthorizationService.AuthorizeAsync(HttpContext.User, null, item.Policy)` | Filters section-contributed top-nav links |

---

## 3. Same-Rule-Different-Spelling Table

Post Phase-1 retirement, controllers and views express the same authorization rule by referencing the same `PolicyNames` constant — the controller via the `[Authorize(Policy = ...)]` attribute, the view via the `authorize-policy="..."` TagHelper attribute (or `(await AuthService.AuthorizeAsync(User, PolicyNames.X)).Succeeded` when a boolean is needed). The legacy `RoleChecks.*` / `ShiftsRoles.*` helpers are invoked from no view, and the Events Guide section's controllers and `_Layout.cshtml` dropdown both resolve through `PolicyNames.EventsAdminOrAdmin`.

| Rule | Controller Spelling | View Spelling |
|---|---|---|
| Admin only | `[Authorize(Policy = PolicyNames.AdminOnly)]` | `authorize-policy="AdminOnly"` |
| Any admin role (admin shell) | `[Authorize(Policy = PolicyNames.AnyAdminRole)]` | `authorize-policy="AnyAdminRole"` |
| Board or Admin | `[Authorize(Policy = PolicyNames.BoardOrAdmin)]` | `authorize-policy="BoardOrAdmin"` |
| TeamsAdmin/Board/Admin | `[Authorize(Policy = PolicyNames.TeamsAdminBoardOrAdmin)]` | `authorize-policy="TeamsAdminBoardOrAdmin"` |
| TeamsAdmin or Admin | `AuthorizationService.AuthorizeAsync(User, PolicyNames.TeamsAdminOrAdmin)` (`TeamAdminController` role-management toggle) | `(await AuthService.AuthorizeAsync(User, PolicyNames.TeamsAdminOrAdmin)).Succeeded` (Team/Details.cshtml "Open store" flag) |
| TicketsAdmin/Board/Admin | `[Authorize(Policy = PolicyNames.TicketAdminBoardOrAdmin)]` | `authorize-policy="TicketAdminBoardOrAdmin"` |
| TicketsAdmin or Admin | `[Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]` | `authorize-policy="TicketAdminOrAdmin"` |
| Scanner access (roles + gate terminal) | `[Authorize(Policy = PolicyNames.ScannerAccess)]` | (no current view spelling) |
| Gate admit (gate write actions) | `[Authorize(Policy = PolicyNames.GateAdmit)]` | (no current view spelling) |
| CampsAdmin or Admin | `[Authorize(Policy = PolicyNames.CampAdminOrAdmin)]` | `authorize-policy="CampAdminOrAdmin"` |
| UsersAdmin/Board/Admin | `[Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]` | `authorize-policy="HumanAdminBoardOrAdmin"` |
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
| App access (`UserState == Active`) | `[Authorize(Policy = PolicyNames.AppAccess)]` | `authorize-policy="AppAccess"` |
| Resource: team coord/admin | `_authorizationService.AuthorizeAsync(User, team, TeamOperationRequirement.{ManageCoordinators, ManageEarlyEntry})` | `Model.IsCurrentUserCoordinator` / `Model.CanManageEarlyEntry` (view-model) |
| Resource: camp lead/admin | `_authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` | `Model.IsCurrentUserLead \|\| Model.IsCurrentUserCampAdmin` (view-model) |
| Resource: camp-event submit | `_authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.SubmitEvent)` | (no view spelling — controller-only) |
| Resource: budget edit | `_authorizationService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)` | `Model.CanEdit` (view-model) |
| Resource: container place/manage | `_authorizationService.AuthorizeAsync(User, target, ContainerOperationRequirement.{Manage, Place})` | `Model.CanX` (view-model) |
| Resource: store order | `authService.AuthorizeAsync(User, order, OrderOperationRequirement.{View, Create, AddLine, RemoveLine, EditCounterparty, Pay, Delete, IssueInvoice, RecordPayment, Refund, DeletePayment})` (and `OrderCreateContext` for Create) | `Model.CanManageByCounterparty` / per-order flags (view-model), including `CanRefund` / `CanDeletePayment` on the order page |
| Resource: expense report | `authService.AuthorizeAsync(User, report, new ExpenseReportOperationRequirement(ExpenseReportOperation.X))` — `View`, `Edit`, `Submit`, `Withdraw`, `Endorse`, `CoordinatorReject`, `DeclineLine`, `Approve`, `FinanceReject`, `RequeueHoldedPush` | `Model.CanX` (view-model) |
| Resource: IBAN access | No resource handler. Finance's `PaymentDetailsAdminController.RevealIban` (`/Finance/Admin/Members/{id}/RevealIban`, behind the Payment Details card on `/Users/Admin/{id}`) is gated by `[Authorize(Policy = PolicyNames.AdminOnly)]` and audits every reveal; expense-report IBAN views render masked with no resource check. `IbanAccessHandler` / `IbanAccessRequirement` were deleted — registered but never constructed, duplicating `AdminOnly`. | (none) |
| Resource: preapproval program | `authorization.AuthorizeAsync(User, program, ProgramOperationRequirement.{Manage, Administer})` | `Model.CanAdminister` (view-model) |
| Preapprovals manage / access (section-local) | `[Authorize(Policy = PreapprovalsPolicies.{Manage, Access})]` | `Model.CanManage` (view-model) |
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
| `Team/Summary.cshtml` / `_AdminTeamRow.cshtml` — Create/Edit links, Deactivate form | `authorize-policy="TeamsAdminBoardOrAdmin"` on the links, `authorize-policy="BoardOrAdmin"` on the Deactivate form | Match `TeamController.CreateTeam` / `EditTeam` (`TeamsAdminBoardOrAdmin`) and `DeleteTeam` (`BoardOrAdmin`) — **OK**. |
| `Ticket/_TicketNav.cshtml` — Backfill / Settings links | `authorize-policy="AdminOnly"` in view | Targets `Shifts/Settings` / Ticket admin actions which have `[Authorize(Policy = PolicyNames.AdminOnly)]` — **OK**. |

### Server-Only (protected endpoint, no visible UI gating)

| Endpoint | Roles | Note |
|---|---|---|
| `GoogleController` actions with broader policies (`Sync`, `SyncPreview`, `ProvisionEmail`, `Resource`, `Human`) and `MonitorController.CheckDriveActivity` | TeamsAdmin/Board/Admin / Board/Admin / UsersAdmin/Board/Admin / UsersAdmin/Admin | Each action has its own policy. |
| `FacilitatedMessageController.SendMessage` (GET/POST `/Email/SendMessage/{id}`) | `[Authorize]` (any authenticated human) | Email serves it; refuses self-send and opted-out recipients. |
| `TeamMessageController.SendMessage` (GET/POST `/Teams/{teamId}/SendMessage/{id}`) | `[Authorize]` + in-method team check | Teams serves send as team; 403 unless the viewer coordinates the active team and its group is linked and synced (no Admin bypass); refuses self-send and opted-out recipients. |
| `MemberOutboxController.AdminOutbox` | `HumanAdminBoardOrAdmin` | Email's "Email Outbox" card on the admin human-detail page links here. |

### Runtime-Only Guards (no attribute, enforced in method body)

These actions rely on `if` checks + early return/forbid instead of `[Authorize(Policy)]`:

| Controller | Action | Guard |
|---|---|---|
| `ShiftAdminController` | All non-public actions | Coordinator-of-department check via `ResolveDepartmentManagementAsync` → `HumansTeamControllerBase.ResolveDepartmentAccessAsync` (resource-based) |
| `TeamAdminController` | Most non-public actions | Coordinator-of-team check via `HumansTeamControllerBase.ResolveTeamManagementAsync` (`TeamOperationRequirement.ManageCoordinators`); `AuthorizationService.AuthorizeAsync(User, PolicyNames.TeamsAdminOrAdmin)` gates the role-management toggle (`Roles`, `EditRole`, `ToggleManagement`) |
| `TeamAdminController` | `EarlyEntry` / `AddEarlyEntry` / `EditEarlyEntry` / `RemoveEarlyEntry` | EE-management check via `ResolveEarlyEntryManagementAsync` → `_authorizationService.AuthorizeAsync(User, team, TeamOperationRequirement.ManageEarlyEntry)` (Admin/TeamsAdmin/Board any team; `EarlyEntryAdmin` any team; coordinator own team) |
| `BudgetController` | `Index`, `Summary`, `CategoryDetail`, line-item CRUD | `_authService.AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` and `_authService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)` |
| `CampController` | All management actions | `_authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` via `HumansCampControllerBase` |
| `ContainerController` | All non-public actions | `_authorizationService.AuthorizeAsync(User, target, ContainerOperationRequirement.{Manage, Place})` (resource-based) |
| `EventsController` | Barrio-event submit/create/edit/update/withdraw | `_authorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.SubmitEvent)` via `HumansCampControllerBase.ResolveCampEventManagementAsync` (resource-based); plus owner-or-`EventsRoles.IsEventsAdmin` gate on Edit/Update endpoints |
| `ExpensesController` | Every report action (View, Edit + line CRUD, Submit, Withdraw, Endorse, CoordinatorReject, DeclineLine, Approve, FinanceReject, RequeueHoldedPush) | `authService.AuthorizeAsync(User, report, new ExpenseReportOperationRequirement(ExpenseReportOperation.X))` (resource-based) |
| `ExpensesController` | `LineEdit` / `LineProofs` read-only view, `Iban` | Inline submitter check on top of the `Edit` grant (`report.SubmitterUserId == userId`) |
| `TeamController` | `EditTeam` (POST) `IsSensitive` flag | `authorizationService.AuthorizeAsync(User, PolicyNames.AdminOnly)` — non-Admin posts leave `IsSensitive` unchanged |
| `StoreController` | Order CRUD/pay/payments | `authService.AuthorizeAsync(User, order, OrderOperationRequirement.*)` (resource-based); `RecordPayment` additionally checks `Refund` when the method is a refund |
| `PreapprovalsAdminController` | `New`; per-program pages and grant POSTs | `PreapprovalsPolicyHandler.IsManagingRole(User)` (Admin/Board/FinanceAdmin) for `New`; `AuthorizeAsync(User, program, ProgramOperationRequirement.{Manage, Administer})` for the rest (resource-based) |
| `IssuesController` | All mutating actions | `_authorization.AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)` (resource-based) |
| `CityPlanningController` / `CityPlanningApiController` | All actions except `Index`/`GetState` | `authorizationService.AuthorizeAsync(User, PolicyNames.CityPlanningMapAdmin)` and lead-of-camp checks; polygon save checks `CampsRoles.IsCampsAdmin(User)`; the container-placement endpoints authorize `ContainerOperationRequirement.Place` |
| `GovernanceBoardVotingController` | `Detail` | `RoleChecks.IsAdmin(User)` drives the admin view-model flag (Finalize affordance) — the `Finalize` POST itself is attribute-gated `AdminOnly` |
| `RoleAssignmentsController.AddRole/EndRole` (`src/Sections/Humans.Auth`) | After `[Authorize(Policy)]` attribute | `authorizationService.AuthorizeAsync(User, roleName, PolicyNames.RoleAssignmentManage)` enforces the role-list filter |
| `ProfileEmailsController` email-edit endpoints | After class-level `[Authorize]` | `_authorizationService.AuthorizeAsync(User, userId, UserEmailOperations.Edit)` (resource-based) |
| `TicketController.Index` | After class-level policy | `FinanceRoles.IsFinanceAdmin(User)` toggles finance-only metrics |
| `MembershipRequiredFilter` | All authenticated requests | Gates the app purely on stored `UserState` (stamped on the principal by `RoleAssignmentClaimsTransformation`): only `Active` reaches the app; `DeletePending` → `/Gdpr/Deletion`, Suspended/AdminSuspended/Rejected/Deleted/Merged → `/Gdpr/Status`, Bare/unseeded → `/OnboardingWidget`. Roles do not bypass the gate. A Backdoor-API-key-authenticated request (`IsMachineRequest` — `AuthenticationType == BackdoorAuthentication.SchemeName`) passes through unconditionally: a machine principal never runs claims transformation, so the state claim is absent by construction. `Deleted`/`Merged` accounts get no exemptions beyond `Account`, `Language` and `Gdpr/Status` — a lingering cookie never reaches a profile. `[AllowAnonymous]` endpoints pass through (`IsPublicEndpoint`), except `Home/Index`, which is the public landing page and the member dashboard on one route and stays gated. For every other state, exempt controllers (`Account`, `OnboardingWidget`, `Consent`, `User`, `Language`, `Guest`, `GovernanceApplications`, `Issues`, `Notifications`, `Survey`), the own-profile maintenance actions on `Profile` (`Index`, `Me`, `Edit`, `Privacy`, `Medical`), `CommunicationPreferences` (`Index`, `UpdatePreference` — own preferences, Email), `TicketParticipation.DeclareNotAttending` / `UndoNotAttending` (own not-attending declaration, Tickets) and `ProfileEmails` (own email/linked-account grid actions), `MemberOutbox.MyOutbox` (own outbox, Email), `CantinaProfile.Dietary` (own dietary form, Cantina), `MemberData.RequestDeletion` / `MemberData.DownloadData` (own deletion request and export, Gdpr), and `[AllowAnonymous]` pass through; other humans' profiles, messaging, search and admin email actions require `Active`. |
| `NameRequiredFilter` | All requests | Global action filter (registered in `Program.cs` before `MembershipRequiredFilter`). Redirects any authenticated user with no real `BurnerName` to the name form; never blocks sign-in (only redirects). A Backdoor-API-key-authenticated request (`MembershipRequiredFilter.IsMachineRequest`) passes through unconditionally — a JSON client cannot fill in the name form. `Deleted`/`Merged` accounts (names cleared by anonymization) pass through to `MembershipRequiredFilter`'s status wall. Exempt controllers (`Account`, `Language`), exempt actions (`OnboardingWidget/Names`, `Home/Error`, `Home/Privacy`), and `[AllowAnonymous]` endpoints other than `Home/Index` (`MembershipRequiredFilter.IsPublicEndpoint`) pass through. |
| `HangfireAuthorizationFilter` | Hangfire dashboard | `RoleChecks.IsAdminOrBoard(User)`; read-only unless Admin |
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
| Expenses | `ExpenseReportView` |
| Finance | `FinanceAdminOrAdmin` |
| Preapprovals | `PreapprovalsManage`, `PreapprovalsAccess` — section-local names in `PreapprovalsPolicies`, not `PolicyNames` (nothing outside the section gates on them) |
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
| `AnyAdminRole` | Admin, Board, UsersAdmin, TeamsAdmin, CampsAdmin, TicketsAdmin, EventsAdmin, FeedbackAdmin, FinanceAdmin, StoreAdmin, CantinaAdmin, RideshareAdmin, ShiftsNoInfo, ShiftsAdmin, ConsentCoordinator | `PolicyNames.AnyAdminRole` (admin-shell entry-point gate) |
| `BoardOnly` | Board | `PolicyNames.BoardOnly` |
| `BoardOrAdmin` | Board, Admin | `PolicyNames.BoardOrAdmin`, `RoleChecks.IsAdminOrBoard` |
| `HumanAdminBoardOrAdmin` | UsersAdmin, Board, Admin | `PolicyNames.HumanAdminBoardOrAdmin`, `UsersRoleChecks.IsUsersAdminBoardOrAdmin` |
| `HumanAdminOrAdmin` | UsersAdmin, Admin | `PolicyNames.HumanAdminOrAdmin` |
| `TeamsAdminBoardOrAdmin` | TeamsAdmin, Board, Admin | `PolicyNames.TeamsAdminBoardOrAdmin`, `TeamsRoles.IsTeamsAdminBoardOrAdmin` |
| `TeamsAdminOrAdmin` | TeamsAdmin, Admin | `PolicyNames.TeamsAdminOrAdmin` (registered but currently only referenced from `Team/Details.cshtml`'s "Open store" boolean — no controller attribute uses it yet) |
| `CampAdminOrAdmin` | CampsAdmin, Admin | `PolicyNames.CampAdminOrAdmin`, `CampsRoles.IsCampsAdmin` |
| `CampComplianceAccess` | CampsAdmin, Admin OR any team/sub-team coordinator | `PolicyNames.CampComplianceAccess` (composite — `CampComplianceAccessHandler`) |
| `CityPlanningMapAdmin` | CampsAdmin, Admin OR any city-planning team member | `PolicyNames.CityPlanningMapAdmin` (composite — `CityPlanningMapAdminHandler`; gates the `/Settings#city-planning` tab) |
| `TicketAdminBoardOrAdmin` | TicketsAdmin, Admin, Board | `PolicyNames.TicketAdminBoardOrAdmin` |
| `TicketAdminOrAdmin` | TicketsAdmin, Admin | `PolicyNames.TicketAdminOrAdmin` |
| `ScannerAccess` | TicketsAdmin, Admin, Board OR `SystemUserIds.GateTerminal` (by NameIdentifier claim) | `PolicyNames.ScannerAccess` (composite assertion — gate-terminal account admitted by id, not by role) |
| `GateAdmit` | TicketsAdmin, Admin, Board OR `SystemUserIds.GateTerminal` (by NameIdentifier claim) | `PolicyNames.GateAdmit` (composite assertion — gate write actions; same principals as `ScannerAccess` today, kept separate so the write path never rides the read-only Scanner gate and the two can diverge) |
| `FinanceAdminOrAdmin` | FinanceAdmin, Admin | `PolicyNames.FinanceAdminOrAdmin`, `FinanceRoles.IsFinanceAdmin`, `FinanceRoles.IsFinanceAdmin` |
| `EventsAdminOrAdmin` | EventsAdmin, Admin | `PolicyNames.EventsAdminOrAdmin` |
| `CantinaAdminOrAdmin` | CantinaAdmin, Admin | `PolicyNames.CantinaAdminOrAdmin` (Cantina coordinator surface) |
| `RideshareAdminOrAdmin` | RideshareAdmin, Admin | `PolicyNames.RideshareAdminOrAdmin` (Rideshare admin surface: settings, stats, day roster) |
| `StoreCatalogAdmin` | StoreAdmin, FinanceAdmin, Admin | `PolicyNames.StoreCatalogAdmin`, `StoreRoles.CanAdministerStore` |
| `ReviewQueueAccess` | ConsentCoordinator, ShiftsAdmin, Board, Admin | `PolicyNames.ReviewQueueAccess` |
| `ConsentCoordinatorBoardOrAdmin` | ConsentCoordinator, Board, Admin | `PolicyNames.ConsentCoordinatorBoardOrAdmin` |
| `ShiftDashboardAccess` | Admin, ShiftsNoInfo, ShiftsAdmin | `PolicyNames.ShiftDashboardAccess` |
| `ShiftDepartmentManager` | Admin, ShiftsNoInfo, ShiftsAdmin OR any team manager/coordinator | `PolicyNames.ShiftDepartmentManager` (composite — `IsAnyTeamManagerOrCoordinatorHandler`) |
| `VolunteerTrackingWrite` | Admin, ShiftsAdmin | `PolicyNames.VolunteerTrackingWrite` |
| `PrivilegedSignupApprover` | Admin, ShiftsNoInfo | `PolicyNames.PrivilegedSignupApprover`, `ShiftsRoles.IsPrivilegedSignupApprover` |
| `VolunteerManager` | Admin, ShiftsAdmin | `PolicyNames.VolunteerManager`, `ShiftsRoles.IsVolunteerManager` |
| `AppAccess` | `UserState == Active` | `PolicyNames.AppAccess` (single `RequireAssertion` — the nav-visibility gate; replaced the former `IsActiveMember` / `ActiveMemberOrShiftAccess` split) |
| `HumanAdminOnly` | UsersAdmin AND NOT (Admin OR Board) | `PolicyNames.HumanAdminOnly` (composite — `HumanAdminOnlyHandler`) |
| `MedicalDataViewer` | Admin, Board, ShiftsNoInfo | `PolicyNames.MedicalDataViewer`, `ShiftsRoles.CanViewMedical` |
| `RoleAssignmentManage` | (resource-based — the resource is the target role-name string) | `PolicyNames.RoleAssignmentManage` (wraps `RoleAssignmentOperationRequirement.Manage`, owned by `Humans.Auth` — `UsersAdminController.AddRole`/`EndRole` call `AuthorizeAsync(User, roleName, PolicyNames.RoleAssignmentManage)` instead of passing the requirement object directly — see §6) |
| `PreapprovalsManage` | Admin, Board, FinanceAdmin OR a manager of any program | `PreapprovalsPolicies.Manage` (section-local; composite — `PreapprovalsPolicyHandler`) |
| `PreapprovalsAccess` | `PreapprovalsManage` OR anyone with a grant or an open program to apply to | `PreapprovalsPolicies.Access` (section-local; composite — `PreapprovalsPolicyHandler`) |
| `ExpenseReportView` | (resource-based — the resource is the expense report) | `PolicyNames.ExpenseReportView` (wraps `ExpenseReportOperationRequirement(ExpenseReportOperation.View)`, owned by Expenses — `BackdoorFinanceController` calls `AuthorizeAsync(User, report, PolicyNames.ExpenseReportView)` through the named policy instead of the requirement object directly, mirroring `RoleAssignmentManage` above; `ExpensesController` still authorizes the requirement directly — see §6, peterdrier/Humans#1838) |

### Notes on Policy Design

- `ShiftDashboardAccess` and `ShiftDepartmentManager` are intentionally distinct: dashboard access is role-list-based, department manager additionally permits any team manager/coordinator (composite via `IsAnyTeamManagerOrCoordinatorHandler`).
- `AppAccess` is the single nav-visibility gate: `UserState == Active` (the user entered their legal name). A plain `RequireAssertion` — no custom requirement/handler. It replaced the former `IsActiveMember` / `ActiveMemberOrShiftAccess` policies (and there is no separate shift access).
- `CampComplianceAccess` is deliberately broader than `CampAdminOrAdmin`: it short-circuits for CampsAdmin/Admin and otherwise admits any team/sub-team coordinator (composite via `CampComplianceAccessHandler`, reusing the same `IShiftService.GetCoordinatorTeamIdsAsync` lookup as `IsAnyTeamManagerOrCoordinatorHandler`). It gates only the read-only Barrios compliance matrix; the camp-management surface in `CampAdminController` stays CampsAdmin-only.
- `HumanAdminOnly` is a composite policy used for the nav "Humans" link that only shows when the user has UsersAdmin but not the broader Board/Admin access.
- `MedicalDataViewer` is a data-access policy, not a page-access policy. It controls whether the medical reveal is offered within pages the user already has access to; the text itself comes only from the audited `POST /Shifts/Medical/{userId}/Reveal`.
- Operator tooling (`/Debug/*`, `/Backdoor`, `/Agent/Admin/*`, Hangfire) is `BoardOrAdmin` to read; every state-changing POST there is `AdminOnly`.
- `GateAdmit` is deliberately a twin of `ScannerAccess` (same assertion body): the durable gate write surface (`/Gate/Decision`, `/Gate/Claim` POST, `/Gate/ClaimPin`, `/Gate/EndShift`) must never inherit a future loosening of the read-only Scanner gate. Supervisor overrides inside `Decision` are a second factor on top of the policy — the shared `Gate:SupervisorPin`, not an identity check (see §4).
- `AnyAdminRole` gates the admin-shell entry point (`/Admin`). Sidebar items inside the shell are filtered per-item by `AdminSidebarViewComponent` against each item's policy. The role list is derived from the sections' `ISectionRoles` through Auth's `RoleCatalog` (every role with `OpensAdminShell`), so a new section role joins without a Shell edit; `EarlyEntryAdmin` opts out.
- Object-relative policies (coordinator of specific team, camp lead of specific camp, camp-event submitter, budget category for coordinator's department, manageable role for UsersAdmin, expense reports, store orders, containers, issues, user-email edits, agent rate-limit) are implemented as resource-based authorization handlers — see §6.

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
| `PreapprovalsPolicyHandler` | `ManagesPreapprovalsRequirement`, `PreapprovalsAccessRequirement` | Preapprovals | `PreapprovalsManage`, `PreapprovalsAccess` |

The only authorization files left in `src/Humans.Web/` are `Authorization/AuthorizationPolicyExtensions.cs`, `MembershipRequiredFilter.cs`, `NameRequiredFilter.cs`, the claims/identity plumbing (`HttpCurrentUserContext.cs`, `HumansUserClaimsPrincipalFactory.cs`, `RoleAssignmentClaimsTransformation.cs`) and `HangfireAuthorizationFilter.cs` at the project root. The framework-facing plumbing (`PolicyNames`, `RoleNames`, `RoleChecks`, `ISectionRoles`, `HumansControllerBase`, `AuthorizeViewTagHelper`, `SectionNavViewComponent`) lives in `src/Humans.Base/` under the namespaces `Humans.Base.Authorization` / `Humans.Base.Constants` / `Humans.Base.Controllers` / `Humans.Base.TagHelpers`.

### `IAuthorizationService.AuthorizeAsync` Call Sites

| File | Line | Call |
|---|---|---|
| `src/Sections/Humans.Teams/Contracts/HumansTeamControllerBase.cs` | 28 | `AuthorizeAsync(User, team, TeamOperationRequirement.ManageCoordinators)` (`ResolveTeamManagementAsync`) |
| `src/Sections/Humans.Teams/Contracts/HumansTeamControllerBase.cs` | 41 | `AuthorizeAsync(User, team, TeamOperationRequirement.ManageEarlyEntry)` (`ResolveEarlyEntryManagementAsync`) |
| `src/Sections/Humans.Teams/Controllers/TeamController.cs` | 164 | `AuthorizeAsync(User, teamInfo, TeamOperationRequirement.ManageEarlyEntry)` (drives `CanManageEarlyEntry` view-model flag on team details) |
| `src/Sections/Humans.Teams/Controllers/TeamController.cs` | 758 | `AuthorizeAsync(User, PolicyNames.AdminOnly)` (EditTeam POST — `IsSensitive` leave-unchanged guard for non-Admin editors) |
| `src/Sections/Humans.Camps/Contracts/HumansCampControllerBase.cs` | 25 | `AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` (non-public season visibility) |
| `src/Sections/Humans.Camps/Contracts/HumansCampControllerBase.cs` | 54 | `AuthorizeAsync(User, camp, CampOperationRequirement.Manage)` |
| `src/Sections/Humans.Camps/Contracts/HumansCampControllerBase.cs` | 85 | `AuthorizeAsync(User, camp, CampOperationRequirement.SubmitEvent)` |
| `src/Sections/Humans.Budget/Controllers/BudgetController.cs` | 33, 112 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` |
| `src/Sections/Humans.Budget/Controllers/BudgetController.cs` | 91 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` (drives `IsCoordinator` flag alongside a coordinator-team-id lookup) |
| `src/Sections/Humans.Budget/Controllers/BudgetController.cs` | 118, 241 | `AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)` |
| `src/Sections/Humans.Containers/Controllers/ContainerController.cs` | 25 | `AuthorizeAsync(User, target, requirement)` (private `AuthorizeAsync` helper, called from every `Manage`/`Place` gate in the controller, including `ResolveAndAuthorizeAsync`) |
| `src/Sections/Humans.Expenses/Controllers/ExpensesController.cs` | 646 | `AuthorizeAsync(User, owningReport, new ExpenseReportOperationRequirement(ExpenseReportOperation.View))` (Attachment — 404 on denial) |
| `src/Sections/Humans.Expenses/Controllers/ExpensesController.cs` | 887 | `AuthorizeAsync(User, report, new ExpenseReportOperationRequirement(operation))` (private `AllowsAsync` — every other report operation: the per-action gate via the report resolver, and the Detail page's affordances) |
| `src/Sections/Humans.Expenses/Controllers/ExpensesController.cs` | 895 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` (private `IsFinanceAdminAsync` — finance flag handed to the service, Review queue scope, New-report member picker) |
| `src/Sections/Humans.Store/Controllers/StoreController.cs` | 54, 76, 79–86, 150, 191, 222, 225, 244, 312, 339, 368, 393 | `AuthorizeAsync(User, order/resource, OrderOperationRequirement.X)` — the order page resolves its `Refund`/`DeletePayment` flags at 85–86; `RecordPayment` 222 plus `Refund` 225 when the method is a refund, `DeletePayment` 244; line-deadline-aware `AddLine`/`RemoveLine` at 339/368 authorize against an `OrderLineContext` |
| `src/Sections/Humans.Store/Controllers/StoreController.cs` | 115, 132 | `AuthorizeAsync(User, new OrderLineContext(...), OrderOperationRequirement.{AddLine, RemoveLine})` (private `FilterLineEditAffordancesAsync` helper — drives the per-product/per-line "can I still edit" affordances shown on the order page) |
| `src/Sections/Humans.Store/Controllers/StoreController.cs` | 263, 286 | `AuthorizeAsync(User, new OrderCreateContext(...), OrderOperationRequirement.Create)` (camp order `Create`, team order `CreateTeamOrder`) |
| `src/Sections/Humans.Issues/Controllers/IssuesController.cs` | 216, 304, 344, 377, 410, 444 | `AuthorizeAsync(User, issue, IssuesOperationRequirement.Handle)` |
| `src/Sections/Humans.CityPlanning/Controllers/CityPlanningController.cs` | 28 | `AuthorizeAsync(User, PolicyNames.CityPlanningMapAdmin)` (map-admin gate) |
| `src/Sections/Humans.CityPlanning/Controllers/CityPlanningController.cs` | 281 | `AuthorizeAsync(User, new ContainerAuthorizationTarget(userCamp.Id) { Year = year }, ContainerOperationRequirement.Place)` (a camp lead with no map-admin policy opens the map for their own camp) |
| `src/Sections/Humans.CityPlanning/Controllers/CityPlanningApiController.cs` | 38 | `AuthorizeAsync(User, PolicyNames.CityPlanningMapAdmin)` (map-admin gate) |
| `src/Sections/Humans.CityPlanning/Controllers/CityPlanningApiController.cs` | 205, 282, 309, 346 | `AuthorizeAsync(User, ContainerAuthorizationTarget.For(container), ContainerOperationRequirement.Place)` (per-container `canEdit` flag in the listing; container placement save, notes, clear) |
| `src/Sections/Humans.Surveys/Controllers/SurveyAdminController.cs` | 122, 181, 187, 197, 222, 254, 283, 319, 371, 618, 656 | `AuthorizeAsync(User, detail, new SurveyOperationRequirement(SurveyOperation.X))` — `Submit`, `Edit`, `Preview`, `ViewResults` |
| `src/Sections/Humans.Workgroups/Controllers/WorkgroupsController.cs` | 448, 451 | `AuthorizeAsync(User, workgroup, WorkgroupOperationRequirement.{Administer, Member})` (page permissions) |
| `src/Sections/Humans.Workgroups/Controllers/WorkgroupsController.cs` | `ApproveMember`, `DeclineMember`, `AddMember` (`ManageMembersAsync`) | Coordinator-only membership decisions: Board/Admin (`Administer`) or a current coordinator of an Active group, else `Forbid()`; the service re-checks via `RequireCoordinator` (`WorkgroupService.Helpers.cs`) unless `asAdmin` |
| `src/Sections/Humans.Preapprovals/Controllers/PreapprovalsAdminController.cs` | 174 | `AuthorizeAsync(User, program, ProgramOperationRequirement.{Manage, Administer})` (`AuthorizedAsync` helper — every per-program page and POST) |
| `src/Sections/Humans.Preapprovals/Controllers/PreapprovalsController.cs` | 38 | `AuthorizeAsync(User, PreapprovalsPolicies.Manage)` (the Manage button on the member page) |
| `src/Sections/Humans.Users/Controllers/ProfileEmailsController.cs` | 184, 222, 273, 315, 352, 372, 428, 503, 527, 565, 591, 761, 787, 831 | `AuthorizeAsync(User, userId, UserEmailOperations.Edit)` (the email-edit endpoints) |
| `src/Sections/Humans.Tickets/ViewComponents/OnsiteChipViewComponent.cs` | 25 | `AuthorizeAsync(UserClaimsPrincipal, PolicyNames.TicketAdminBoardOrAdmin)` (onsite-chip visibility gate for a non-self viewer) |
| `src/Sections/Humans.Users/Controllers/ProfileViewController.cs` | 174 | `AuthorizeAsync(User, PolicyNames.PrivilegedSignupApprover)` (drives `isPrivilegedApprover` — gates whether a non-own-profile viewer sees the "sent messages" panel on the profile page; admits coordinators or `PrivilegedSignupApprover` role) |
| `src/Sections/Humans.Auth/Controllers/RoleAssignmentsController.cs` | 105 | `AuthorizeAsync(User, model.RoleName, PolicyNames.RoleAssignmentManage)` (AddRole — goes through the named policy rather than passing `RoleAssignmentOperationRequirement.Manage` directly; see §5) |
| `src/Sections/Humans.Auth/Controllers/RoleAssignmentsController.cs` | 143 | `AuthorizeAsync(User, roleAssignment.RoleName, PolicyNames.RoleAssignmentManage)` (EndRole) |
| `src/Sections/Humans.GoogleIntegration/Controllers/TeamResourcesController.cs` | 273 | `AuthorizeAsync(User, team, TeamResourceManageRequirement.Instance)` (resource-based team-resource management gate) |
| `src/Sections/Humans.Backdoor/Controllers/BackdoorFinanceController.cs` | 287, 290 | `AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` (finance-wide routes) and `AuthorizeAsync(User, report, PolicyNames.ExpenseReportView)` (report/attachment routes — named policy, same as `RoleAssignmentManage` above; peterdrier/Humans#1838) |
| `src/Sections/Humans.Agent/Controllers/AgentController.cs` | 59 | `AuthorizeAsync(User, user.Id, [new AgentRateLimitRequirement()])` (requirement instantiated directly, no `PolicyNames` constant) |
| `src/Sections/Humans.Settings/ViewComponents/SettingsTabComposition.cs` | 30 | `AuthorizeAsync(user, null, tab.Policy)` (filters the `/Settings` tabs sections contribute) |
| `src/Sections/Humans.Settings/ViewComponents/EventSettingsTabViewComponent.cs` | 24 | `AuthorizeAsync(UserClaimsPrincipal, null, PolicyNames.AdminOnly)` (Event tab editable vs read-only) |
| `src/Humans.Base/TagHelpers/AuthorizeViewTagHelper.cs` | 54 | `AuthorizeAsync(user, Policy)` (driver of `<authorize-policy>` view tags) |
| `src/Humans.Base/ViewComponents/SectionNavViewComponent.cs` | 64 | `AuthorizeAsync(HttpContext.User, null, item.Policy)` (filters section nav items) |
| `src/Humans.Web/ViewComponents/AdminNavItems.cs` | 69 | `AuthorizeAsync(user, null, item.Policy)` (filters admin sidebar groups and tabs) |
| `src/Humans.Web/ViewComponents/AdminSidebarViewComponent.cs` | 26 | `AuthorizeAsync(HttpContext.User, PolicyNames.AnyAdminRole)` (dashboard link) |
| `src/Humans.Web/ViewComponents/AdminSummaryViewComponent.cs` | 34 | `AuthorizeAsync(HttpContext.User, null, tile.Policy)` (filters admin dashboard tiles) |
| `src/Humans.Web/Middleware/PrivateUploadMiddleware.cs` | 34 | `AuthorizeAsync(principal, context, PolicyNames.AdminOnly)` (private feedback uploads; issue uploads go through `IIssueTriage` visibility instead) |

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

### Roles

Only `Admin` and `Board` are `RoleNames` constants in Base. Every other role is a `<Section>Roles` constant in its owning section's Contracts, declared through `ISectionRoles` and composed into Auth's `RoleCatalog`; six carry a former name (`HumanAdmin`, `CampAdmin`, `TicketAdmin`, `VolunteerCoordinator`, `NoInfoAdmin`, `EETeamAdmin`) that the `auth.role-renames` data migration retires.

| Role | Stored value |
|---|---|
| `Admin` | `"Admin"` |
| `Board` | `"Board"` |
| `ConsentCoordinator` | `"ConsentCoordinator"` |
| `ShiftsAdmin` | `"ShiftsAdmin"` |
| `TeamsAdmin` | `"TeamsAdmin"` |
| `CampsAdmin` | `"CampsAdmin"` |
| `TicketsAdmin` | `"TicketsAdmin"` |
| `ShiftsNoInfo` | `"ShiftsNoInfo"` |
| `EventsAdmin` | `"EventsAdmin"` |
| `FeedbackAdmin` | `"FeedbackAdmin"` |
| `UsersAdmin` | `"UsersAdmin"` |
| `FinanceAdmin` | `"FinanceAdmin"` |
| `StoreAdmin` | `"StoreAdmin"` |
| `CantinaAdmin` | `"CantinaAdmin"` |
| `EarlyEntryAdmin` | `"EarlyEntryAdmin"` |
| `RideshareAdmin` | `"RideshareAdmin"` |

### Role predicates → Canonical Policy Mapping

`RoleChecks` (Base) keeps the Admin/Board predicates; each section's `<Section>Roles` class carries its own.

| Method | Canonical Policy |
|---|---|
| `IsActiveMember` | `AppAccess` (same `UserState == Active` check) |
| `IsAdmin` | `AdminOnly` |
| `IsBoard` | (no standalone policy) |
| `IsAdminOrBoard` | `BoardOrAdmin` |
| `TeamsRoles.IsTeamsAdmin` | (no standalone policy — used by Store's privileged-reader check and `OrderAuthorizationHandler`) |
| `TeamsRoles.IsTeamsAdminBoardOrAdmin` | `TeamsAdminBoardOrAdmin` |
| `CampsRoles.IsCampsAdmin` | `CampAdminOrAdmin` |
| `UsersRoleChecks.IsUsersAdminBoardOrAdmin` | `HumanAdminBoardOrAdmin` |
| `UsersRoleChecks.IsUsersAdmin` | `HumanAdminOnly` (composite, when negated against Board/Admin) |
| `FinanceRoles.IsFinanceAdmin` | `FinanceAdminOrAdmin` |
| `StoreRoles.CanAdministerStore` | `StoreCatalogAdmin` |
| `IsVolunteerManager` | `VolunteerManager` |
| `RoleAssignmentRules.Assignable` (Auth) | `RoleAssignmentOperationRequirement.Manage` (resource-based, see §6) |

### ShiftsRoles Methods → Canonical Policy Mapping

| Method | Canonical Policy |
|---|---|
| `IsPrivilegedSignupApprover` | `PrivilegedSignupApprover` |
| `CanManageDepartment` | `ShiftDepartmentManager` (role-list portion; composite extends with team-manager OR) |
| `CanViewMedical` | `MedicalDataViewer` |
