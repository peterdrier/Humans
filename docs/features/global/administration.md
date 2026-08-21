<!-- freshness:triggers
  src/Humans.Web/Controllers/AdminController.cs
  src/Sections/Humans.Users/Controllers/UsersAdminAccountMergesController.cs
  src/Sections/Humans.Users/Controllers/UsersAdminController.cs
  src/Sections/Humans.Users/Controllers/ProfileController.cs
  src/Sections/Humans.GoogleIntegration/Controllers/GoogleController.cs
  src/Humans.Base/Authorization/PolicyNames.cs
  src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs
  src/Humans.Web/Authorization/RoleAssignmentClaimsTransformation.cs
  src/Humans.Web/Views/Admin/**
  src/Sections/Humans.Governance/Views/Governance/**
  src/Humans.Base/Constants/RoleNames.cs
  src/Humans.Base/Constants/RoleGroups.cs
-->
<!-- freshness:flag-on-change
  Admin/Profile/Google route tables, role catalog, dashboard metrics, and the Board-vs-Admin role split — review when admin-area controllers, role names, or authorization policies change.
-->

# Administration

## Business Context

System administrators need comprehensive tools to manage members, review applications, oversee teams, and maintain organizational compliance. The admin interface provides dashboards and management screens for all key operations.

## User Stories

### US-9.1: View Admin Dashboard
**As an** administrator
**I want to** see an overview of system status
**So that** I can quickly identify items needing attention

**Acceptance Criteria:**
- Total member count
- Active vs inactive members
- Pending applications count
- Pending consent reminders
- Quick action links
- System health indicators

### US-9.2: Search and View Members
**As an** administrator
**I want to** search for and view member details
**So that** I can assist with member issues

**Acceptance Criteria:**
- Search by email or display name
- Paginated results
- Click through to member detail
- View full profile including legal name
- See application history and consent status

### US-9.3: Suspend/Unsuspend Members
**As an** administrator
**I want to** suspend or unsuspend member accounts
**So that** I can enforce organizational rules

**Acceptance Criteria:**
- Suspend with required notes
- Unsuspend clears suspension
- Status updates immediately
- Action logged for audit
- Notification sent to member

### US-9.4: Review Applications
**As an** administrator
**I want to** review and process Asociado applications
**So that** qualified applicants can join

**Acceptance Criteria:**
- Filter by status
- View full application details
- Start review, approve, reject, request info
- Add notes visible to applicant
- See complete state history

### US-9.5: Manage Teams
**As an** administrator
**I want to** create and manage teams
**So that** the organization has appropriate working groups

**Acceptance Criteria:**
- View all teams (user-created and system)
- Create new teams
- Edit team settings (non-system only)
- Deactivate teams
- View member counts and pending requests

## UserState and Volunteers Provisioning

Human app access is `UserState == Active`, set when the human enters their legal name. There is no manual "approve volunteer" admin action. The Volunteers system team is a Google Workspace provisioning group reconciled from name + consents by `SystemTeamSyncJob`; consent-review status remains an audit/safety workflow.

## Controller Routes

There is no `/Board/*` route prefix. `BoardController` was removed in nobodies-collective/Humans#499 and its work redistributed: the dashboard to `AdminController` (`/Admin`), human management to `UsersAdminController` (`/Users/Admin`), Google sync to `GoogleController`, and the global audit log to `AuditLogController` (`/AuditLog`). **Board** survives as a *role*, not as an area.

### UsersAdminController (`/Users/Admin`) — Human management actions

Human management is now on `UsersAdminController` under `/Users/Admin`, accessible to HumanAdmin, Board, and Admin roles.

| Route | Action | Roles |
|-------|--------|-------|
| `/Users/Admin` | AdminList | HumanAdmin, Board, Admin — member list |
| `/Users/Admin/{id}` | AdminDetail | HumanAdmin, Board, Admin — member detail |
| `/Users/Admin/{id}/Outbox` | AdminOutbox | HumanAdmin, Board, Admin — view email outbox |
| `/Users/Admin/{id}/Suspend` | Suspend | POST: HumanAdmin, Board, Admin |
| `/Users/Admin/{id}/Unsuspend` | Unsuspend | POST: HumanAdmin, Board, Admin |
| `/Users/Admin/{id}/Reject` | Reject | POST: HumanAdmin, Board, Admin |
| `/Users/Admin/{id}/Roles/Add` | AddRole | GET/POST: HumanAdmin, Board, Admin |
| `/Users/Admin/{id}/Roles/{roleId}/End` | EndRole | POST: HumanAdmin, Board, Admin |

### GoogleController (`/Google/`) — Admin (mostly)

Google sync, settings, account provisioning, and audit routes have been extracted from `AdminController` and `BoardController` into `GoogleController`. These were previously documented as `/Admin/*` or `/Board/*` routes.

| Route | Auth | Description |
|-------|------|-------------|
| `/Google/SyncSettings` | Admin | GET/POST: Per-service sync mode configuration |
| `/Google/SyncResults` | Admin | GET: Results of last sync check |
| `/Google/CheckGroupSettings` | Admin | POST: Check Google Group settings for drift |
| `/Google/GroupSettingsResults` | Admin | GET: Display group settings drift results |
| `/Google/RemediateGroupSettings` | Admin | POST: Fix settings drift on one group |
| `/Google/RemediateAllGroupSettings` | Admin | POST: Fix settings drift on all groups |
| `/Google/AllGroups` | Admin | GET: All Google groups |
| `/Google/Sync` | TeamsAdmin, Board, Admin | GET: Sync status page (tabbed: Drive/Groups) |
| `/Google/Sync/Preview/{resourceType}` | TeamsAdmin, Board, Admin | GET: AJAX preview drift |
| `/Google/Sync/Execute/{resourceId}` | Admin | POST: Sync one resource |
| `/Google/Sync/ExecuteAll/{resourceType}` | Admin | POST: Sync all of a type |
| `/Monitor/CheckDriveActivity` (`MonitorController`, not `GoogleController` — nobodies-collective/Humans#866) | Board, Admin | POST: Manual Drive Activity check |
| `/Monitor/Resource/{id}` (`MonitorController`) | Board, Admin | GET: Per-resource sync audit log |
| `/Monitor/Human/{id}` (`MonitorController`) | HumanAdmin, Board, Admin | GET: Per-user sync audit log |
| `/Google/Human/{id}/ProvisionEmail` | Admin | POST: Provision @nobodies.team email |
| `/Google/Accounts` | Admin | GET: All @nobodies.team accounts |
| `/Google/Accounts/Provision` | Admin | POST: Provision account |
| `/Google/Accounts/Suspend` | Admin | POST: Suspend account |
| `/Google/Accounts/Reactivate` | Admin | POST: Reactivate account |
| `/Google/Accounts/ResetPassword` | Admin | POST: Reset password |
| `/Google/Accounts/Link` | Admin | POST: Link existing account |
| `/Google/LinkGroupToTeam` | Admin | POST: Link group to team |
| `/Google/CheckEmailRenames` | Admin | POST: Detect Google-side email renames |
| `/Google/EmailRenames` | Admin | GET: Display the last detected email-rename results |
| `/Google/EmailFlagViolations` | Admin | GET: List users whose email flags (primary/Google/verified) are inconsistent |
| `/Google/SyncOutbox` | Admin | GET: View the last 200 Google sync outbox events |
| `/Google/SyncOutbox/{id}/Requeue` | Admin | POST: Requeue a single failed sync outbox event |
| `/Google/SyncOutbox/RequeueAll` | Admin | POST: Requeue all permanently-failed sync outbox events |
| `/Google/Human/{id}/RerunSync` | Admin | POST: Enqueue Google sync for all of a user's teams |

### Admin dashboard and diagnostics

`AdminController` owns only the shared dashboard. Legacy technical operations now live on `DebugController`; per-human purge lives on `UsersAdminController`.

| Route | Action | Description |
|-------|--------|-------------|
| `/Admin` | Index | Admin dashboard |
| `/Users/Admin/{id}/Purge` | PurgeHuman | POST: Dev/QA user purge (non-production); on `UsersAdminController` |
| `/Debug/Configuration` | Configuration | Configuration status page |
| `/Debug/Logs` | Logs | View recent log entries |
| `/Debug/DbStats` | DbStats | Database query statistics |
| `/Debug/DbStats/Reset` | ResetDbStats | POST: Reset query statistics |
| `/Debug/CacheStats` | CacheStats | Cache hit/miss statistics per key type |
| `/Debug/CacheStats/Reset` | ResetCacheStats | POST: Reset cache statistics |
| `/Debug/DbVersion` | DbVersion | Database migration version |
| `/Debug/Maintenance/ClearHangfireLocks` | ClearHangfireLocks | POST: Admin-only lock cleanup |

### UsersAdminAccountMergesController (`/Users/Admin/AccountMerges/`) — Admin only

The single unified account-merge surface (PR #899 consolidation). It combines duplicate-account detection (`IDuplicateAccountService`, detection-only) and user-submitted merge requests (`IAccountMergeService`) into one queue. The old separate `/Admin/DuplicateAccounts` and `/Admin/MergeRequests` screens (and their controllers) were deleted. Both `AccountMergeService` and `DuplicateAccountService` are now Users-section services.

| Route | Action | Description |
|-------|--------|-------------|
| `/Users/Admin/AccountMerges` | Index | Unified queue of pending merge requests + detected duplicate pairs |
| `/Users/Admin/AccountMerges/Merge` | Merge | POST: Merge an ad-hoc survivor/archived pair |
| `/Users/Admin/AccountMerges/{requestId}/Merge` | MergeRequest | POST: Accept a merge request (admin picks survivor) |
| `/Users/Admin/AccountMerges/{requestId}/Dismiss` | Dismiss | POST: Reject a merge request, no account changes |
| `/Users/Admin/AccountMerges/{requestId}/Close` | Close | POST: Reconcile an orphan request whose accounts already merged |

## Dashboard Metrics

### AdminDashboardViewModel
```csharp
public sealed record AdminDashboardViewModel(
    string GreetingFirstName,
    int TotalUsers,
    int ActiveProfileUsers,
    int TicketHolders,
    int ShiftCoveragePercent,
    int? ShiftFilledOf,
    int? ShiftTotalOf,
    int OpenFeedback,
    int OnlineNow,
    int OnlineLastHour,
    int OnlineLast24h,
    IReadOnlyList<DepartmentCoverage> StaffingByDepartment,
    IReadOnlyList<DashboardActivityRow> RecentActivity,
    DashboardApplicationStats AppStats,
    IReadOnlyList<DashboardLanguageCount> LanguageDistribution,
    UserSetMembership SetMembership,
    int TotalTeams,
    int TotalAuditEvents,
    int TotalEmails,
    int StoreOrders,
    decimal StoreTotalEur,
    int ExpenseReports,
    decimal ExpenseTotalEur);
```

`AdminController.Index` builds these from a single `IUserServiceRead.GetAllUserInfosAsync` snapshot (counts derived from `UserInfo.IsActive` / `HasTicketForYear`), the active event from `IBurnSettingsService.GetActiveAsync`, shift coverage from `IShiftManagementServiceRead.GetOverallCoverageAsync`, actionable feedback from `IFeedbackServiceRead`, recent audit rows from `IAuditViewerService`, application/language/set-membership stats from `IAdminDashboardService`, team count from `ITeamServiceRead`, audit event total from `IAuditViewerService.GetPageAsync`'s `TotalCount`, email outbox total from `IEmailOutboxServiceRead.GetOutboxStatsAsync`, the active event year's store order count/total from `IStoreServiceRead.GetStoreSummaryAsync` (zero when there's no active event), and the all-status expense report count/total from `IExpenseReportServiceRead.GetAllAsync` — not from direct table queries. `OpenFeedback` is computed for every viewer but the view wraps it in `authorize-policy="AdminOnly"` — Board members and domain admins don't see it in the summary line, matching Feedback triage itself being Admin only.

## Member Management

### Member List View
```
┌─────────────────────────────────────────────────────────┐
│ Members                               [Search: ____]    │
├─────────────────────────────────────────────────────────┤
│ Photo │ Name           │ Email          │ Status │ View │
│ ───── │ ────────────── │ ────────────── │ ────── │ ──── │
│ [img] │ Alice Johnson  │ alice@...      │ Active │ [→]  │
│ [img] │ Bob Smith      │ bob@...        │ Inactive│ [→]  │
│ ...   │ ...            │ ...            │ ...    │ ...  │
├─────────────────────────────────────────────────────────┤
│ Showing 1-20 of 156                    [< 1 2 3 4 >]    │
└─────────────────────────────────────────────────────────┘
```

### Member Detail View
```
┌─────────────────────────────────────────────────────────┐
│ [Photo]  Alice Johnson                                  │
│          alice@nobodies.es                              │
│          Member since Jan 15, 2024                      │
├─────────────────────────────────────────────────────────┤
│ PROFILE INFORMATION                                     │
│ Legal Name: Alice Marie Johnson                         │
│ Phone: +34 612 345 678                                  │
│ Location: Madrid, ES                                    │
│ Status: [Active]                                        │
├─────────────────────────────────────────────────────────┤
│ APPLICATIONS (2)                                        │
│ • Approved - Jan 20, 2024                              │
│ • Withdrawn - Jan 10, 2024                             │
├─────────────────────────────────────────────────────────┤
│ CONSENTS (3/3)                                          │
│ ✓ Privacy Policy                                        │
│ ✓ Terms and Conditions                                  │
│ ✓ Code of Conduct                                       │
├─────────────────────────────────────────────────────────┤
│ ADMIN ACTIONS                                           │
│ [Suspend Member]  Notes: [_______________]              │
└─────────────────────────────────────────────────────────┘
```

## Application Management

### Application List
- **Default filter**: Pending (Submitted)
- **Sort**: By submission date (oldest first)
- **Columns**: Applicant, Email, Status, Submitted, Motivation preview

### Application Detail
```
┌─────────────────────────────────────────────────────────┐
│ Application #abc123                                     │
│ Status: [Submitted]                                     │
├─────────────────────────────────────────────────────────┤
│ APPLICANT                                               │
│ [Photo] Bob Smith                                       │
│         bob@email.com                                   │
├─────────────────────────────────────────────────────────┤
│ MOTIVATION                                              │
│ "I want to join because..."                             │
│                                                         │
│ ADDITIONAL INFO                                         │
│ "I have experience with..."                             │
├─────────────────────────────────────────────────────────┤
│ TIMELINE                                                │
│ • Submitted: Jan 15, 2024 10:30                        │
├─────────────────────────────────────────────────────────┤
│ ACTIONS                     Notes: [_______________]    │
│ [Approve] [Reject]                                      │
└─────────────────────────────────────────────────────────┘
```

### Application Actions

| Action | From Status | Result | Notes Required |
|--------|-------------|--------|----------------|
| Approve | Submitted | Approved | Optional (DecisionNote) |
| Reject | Submitted | Rejected | Yes (DecisionNote required) |

## Team Management

### Team List View
```
┌─────────────────────────────────────────────────────────┐
│ Teams                                    [Create Team]  │
├─────────────────────────────────────────────────────────┤
│ Name      │ Type      │ Members │ Pending │ Actions    │
│ ───────── │ ───────── │ ─────── │ ─────── │ ────────── │
│ Volunteers│ System    │ 45      │ 0       │ (managed)  │
│ Coordinators│ System  │ 8       │ 0       │ (managed)  │
│ Board     │ System    │ 5       │ 0       │ (managed)  │
│ Events    │ Approval  │ 12      │ 3       │ [Edit][Del]│
│ Tech      │ Open      │ 7       │ 0       │ [Edit][Del]│
└─────────────────────────────────────────────────────────┘
```

### Create/Edit Team Form
```
┌─────────────────────────────────────────────────────────┐
│ Create Team                                             │
├─────────────────────────────────────────────────────────┤
│ Name:        [___________________________]              │
│                                                         │
│ Description: [___________________________]              │
│              [___________________________]              │
│                                                         │
│ [✓] Require approval to join                           │
│                                                         │
│ [Create Team]  [Cancel]                                 │
└─────────────────────────────────────────────────────────┘
```

## Authorization

### Role Separation: Board vs Admin

Board and Admin are **roles**, not areas — there is one admin route prefix (`/Admin/`, `AdminController`, Admin only except where noted). Governance operations that used to sit behind `/Board/` now live on the owning section's own routes (`/Users/Admin`, `/Governance/*`, `/AuditLog`, `/Google/*`) and are gated per-route by role.

Board members reach governance operations — member management, applications, teams, roles, audit log — through those section routes. Admin-only technical operations (configuration, sync settings, Hangfire, email preview, system team sync) stay under `/Admin/`.

A user can hold both roles simultaneously. Admin is a superset for role assignment purposes.

### Additional Roles

All roles are defined in `RoleNames` constants and use temporal `RoleAssignment` records (same as Board and Admin).

| Role | Purpose |
|------|---------|
| **HumanAdmin** | View human admin pages, suspend/reject humans, provision @nobodies.team accounts, manage role assignments. Does NOT include Board or Admin capabilities. |
| **TeamsAdmin** | System-wide team management (edit teams, approve joins, assign coordinators, configure Google Group prefixes). Can view sync status at `/Google/Sync` but cannot execute sync actions. |
| **CampAdmin** | Manage camps, approve/reject season registrations, configure camp settings system-wide. |
| **TicketAdmin** | Manage ticket vendor integration, trigger syncs, generate discount codes, export ticket data. |
| **NoInfoAdmin** | Approve/voluntell shift signups (cannot create/edit shifts). Access to volunteer event profile medical data. |
| **EventsAdmin** | Event Guide dashboard, moderation, settings, categories, venues, and export. |
| **FeedbackAdmin** | None since nobodies-collective/Humans#977 — Feedback is retired and every screen is `AdminOnly`. The role stays assignable (Staff page, Guide, `AnyAdminRole`) but grants no feedback access. |
| **FinanceAdmin** | Manage budgets, budget years, groups, categories, and line items. Full Finance section access. |
| **StoreAdmin** | Store catalog, summary, and payments. |
| **CantinaAdmin** | Cantina weekly roster. |
| **ConsentCoordinator** | Safety checks on new humans during onboarding. Can clear or flag consent checks. |
| **VolunteerCoordinator** | Read-only access to onboarding review queue. |
| **EETeamAdmin** | Cross-team Early-Entry administrator — grant/edit/revoke early-entry grants on any team that has early entry enabled. Confers nothing else; team coordinators manage EE on their own team without this role. |

### Authorization Foundation

The app uses both role-based `[Authorize(Roles = "...")]` attributes (on controllers) and policy-based `[Authorize(Policy = "...")]` attributes (on views and tag helpers via `PolicyNames` constants). Policies are defined in `AuthorizationPolicyExtensions` and registered in `Program.cs`. Policy names are in `PolicyNames` constants.

Role claims are synced from the `RoleAssignment` table to Identity claims via `RoleAssignmentClaimsTransformation` (an `IClaimsTransformation`). This makes `User.IsInRole()` and `[Authorize(Roles = "...")]` work correctly based on temporal role assignments.

### Role Assignment Authorization
Resource-based: `UsersAdminController.AddRole`/`EndRole` call `IAuthorizationService.AuthorizeAsync(User, roleName, PolicyNames.RoleAssignmentManage)`, evaluated by `RoleAssignmentAuthorizationHandler` (`Humans.Auth`) against the target role name (the resource):
- **Admin** can assign/end any role
- **Board** or **HumanAdmin** can assign/end any role in `RoleNames.BoardManageableRoles` — Board, HumanAdmin, TeamsAdmin, CampAdmin, TicketAdmin, NoInfoAdmin, FeedbackAdmin, FinanceAdmin, EventsAdmin, StoreAdmin, CantinaAdmin, EETeamAdmin, ConsentCoordinator, VolunteerCoordinator (not Admin)
- Everyone else is denied
- Attempting to assign a role outside your permissions returns 403 Forbidden; ending one outside your permissions returns 404 (the row is treated as not found rather than as a permissions error)

### Hangfire Dashboard
- Restricted to **Admin** role only via `HangfireAuthorizationFilter`

### Role Assignment
- Configured via `RoleAssignment` with temporal validity (ValidFrom/ValidTo)
- Created by existing Admin or Board member (within their permissions)
- Bootstrap: First Admin must be created directly in the database

## Audit Logging

All admin actions are logged via Serilog:
```csharp
_logger.LogInformation(
    "Admin {AdminId} {Action} member {MemberId}",
    currentUser.Id, "suspended", memberId);
```

### Logged Actions
- Member suspension/unsuspension
- Application status changes
- Team creation/modification
- Role assignments

## Quick Actions (Dashboard)

### Admin Dashboard (`/Admin`)

| Action | Link | Badge |
|--------|------|-------|
| Sync Settings | `/Google/SyncSettings` | - |
| Configuration Status | `/Debug/Configuration` | - |
| Background Jobs | `/hangfire` | - |
| Check Group Settings | `/Google/CheckGroupSettings` | - |

## System Health

### Dashboard Indicators
- **Database Connection**: Green if responsive
- **Background Jobs**: Green if Hangfire server active
- **Health Check URL**: `/health/ready`
- **Sync System Teams**: Button to manually trigger `SystemTeamSyncJob.ExecuteAsync()`, which recalculates membership for Volunteers, Coordinators, and Board teams. Useful for fixing users whose name or consent state changed before the scheduled sync ran.

### Prometheus Metrics
- Available at `/metrics`
- Scraped by monitoring infrastructure

## Related Features

- [Authentication](../../../src/Sections/Humans.Auth/Docs/features/authentication.md) - Admin role authorization
- [Asociado Applications](../../../src/Sections/Humans.Governance/Docs/features/asociado-applications.md) - Voting member application review
- [Teams](../../../src/Sections/Humans.Teams/Docs/features/Teams-feature.md) - Team management
- [Background Jobs](../global/background-jobs.md) - Hangfire dashboard
