<!-- freshness:triggers
  src/Humans.Web/Views/Admin/**
  src/Humans.Web/Views/Shared/_AdminLayout.cshtml
  src/Humans.Web/ViewComponents/AdminNavTree.cs
  src/Humans.Web/Views/UsersAdmin/AdminList.cshtml
  src/Humans.Web/Views/UsersAdmin/AdminDetail.cshtml
  src/Humans.Web/Views/UsersAdminAccountMerges/**
  src/Humans.Web/Views/Notifications/**
  src/Humans.Web/Controllers/AdminController.cs
  src/Humans.Web/Controllers/UsersAdminAccountMergesController.cs
  src/Humans.Web/Controllers/UsersAdminController.cs
  src/Humans.Web/Controllers/AdminLegalDocumentsController.cs
  src/Humans.Web/Controllers/NotificationsController.cs
  src/Humans.Application/Services/AuditLog/**
  src/Humans.Application/Services/Notifications/**
  src/Humans.Application/Services/Users/AccountMergeService.cs
  src/Humans.Application/Services/Users/DuplicateAccountService.cs
  src/Humans.Application/Services/Auth/RoleAssignmentService.cs
  src/Humans.Application/Services/Users/AccountProvisioningService.cs
  src/Humans.Application/Services/GoogleIntegration/SyncSettingsService.cs
-->
<!-- freshness:flag-on-change
  Global control panel — humans list, audit log, notifications, sync settings, duplicate/merge resolution, and admin diagnostics. Review when admin views, role-management surface, or sync-mode plumbing changes.
-->

# Admin

## What this section is for

The Admin section is the global control panel: managing humans (suspending, rejecting, assigning roles, purging in non-production), configuring Google sync, reading the audit log, triaging the notification inbox, and running technical operations like configuration review, in-memory logs, database version, cache and query stats, and Hangfire lock cleanup.

Admin is layered. **Board** and **HumanAdmin** can do human management — the list, detail, role assignments, suspend/unsuspend, and reject. **Admin** is the superset and additionally owns technical operations, sync settings, duplicate-account resolution, and workspace-account provisioning. Domain admins like Teams Admin, Camp Admin, and Ticket Admin are separate roles covered in their own section guides.

![TODO: screenshot — Admin dashboard home showing humans summary, recent audit entries, and sync status]

## Key pages at a glance

- `/Admin` — the admin dashboard: summary tiles (humans in review, open feedback, pending shifts, recent audit activity) wrapped in the admin shell, with a left sidebar grouping every admin tool (Tickets, Members, Shifts, Barrios, Cantina, Money, Event Guide, Governance, Audit, Feedback, Messaging, and below a divider the system groups Google, Agent, Legal, Diagnostics, Dev, Design, Temp). Reachable by any admin-shaped role; each sidebar item appears only if you're authorized for it.
- `/Users/Admin` — humans list; filter by `UserState` with values like `?filter=bare`, `?filter=active`, `?filter=suspended`, `?filter=rejected`, and `?filter=deleting`.
- `/Users/Admin/{id}` — per-human detail, with suspend, unsuspend, reject, add role, and end role.
- `/Users/Admin/{id}/Outbox` — per-human email outbox.
- `/AuditLog` — global audit log, filterable and paginated.
- `/Notifications` — your notification inbox.
- `/Google/SyncSettings` — per-service sync mode (Admin only).
- `/Debug/Configuration`, `/Debug/Logs`, `/Debug/DbStats`, `/Debug/CacheStats`, `/Debug/DbVersion` — technical diagnostics.
- `/Debug/Maintenance/ClearHangfireLocks` — clear stuck job locks (Admin only; requires restart).
- `/Users/Admin/AccountMerges` — the unified duplicate-detection + merge-request queue.
- `/Users/Admin/{id}/Purge` — permanent delete, disabled in production.
- `/hangfire` — Hangfire dashboard, Admin only.

## As a Volunteer

Admin pages are not visible to you.

## As a Coordinator

The Coordinator role does not include global admin access; the domain-specific admin roles (Teams Admin, Camp Admin, Ticket Admin, Finance Admin, Feedback Admin, and so on) are separate and covered in their respective section guides.

## As a Board member / Admin

### Work the dashboard

Open `/Admin` for the dashboard. The summary tiles — humans in review, open feedback, pending shifts, and recent audit activity — give you a fast read on what needs attention, and the recent-activity feed shows the latest audit entries so you can see what the system and other admins have been doing. The left sidebar groups every admin tool you have access to. What you see is scoped to your roles: an Admin sees everything; a Board member sees the Members and Governance tools; a domain admin sees just their own area.

### Manage humans

Open `/Users/Admin`. Search by name or email, filter by `UserState` (`bare`, `active`, `suspended`, `rejected`, `deleting`, `merged`, or `deleted`) or by sync state (`googlerejected` — humans whose Google Workspace email has been permanently rejected by the sync), and click through to a human's detail page. From there you can:

- **Reject** a signup. Audited, and the human is notified. (There is no "approve volunteer" action — app access is automatic once the human enters their legal name (`UserState == Active`); Volunteers-team / Google Workspace provisioning follows from name + consents via the scheduled sync.)
- **Suspend** or **Unsuspend**. Suspension requires a note; it revokes Google Workspace access on the next sync and ends current memberships. Unsuspension clears the flag and re-queues access.
- **Add role** or **End role**. Role assignments are temporal — valid-from plus optional valid-to — and every change is audited. **Admin** can assign any role. **Board** and **HumanAdmin** can assign any role **except** Admin. The first Admin must be seeded directly in the database.

Every human-admin action writes an audit entry with your user as the actor.

### Read the audit log

`/AuditLog` shows every audit entry — role changes, suspensions, team join decisions, Google sync events, tier application decisions, anomalous Drive permission changes, workspace-account lifecycle, and more. Filter buttons scope to Anomalous Permissions, Access Granted/Revoked, Suspensions, and Roles. The log is **append-only** — database triggers prevent update and delete — and deleted humans show as "Deleted User" rather than disappearing from the trail. Per-human and per-resource audit views are also linked from the human and resource detail pages.

### Triage the notification inbox

`/Notifications` is your shared "what needs my attention" view. Actionable notifications targeted at the Admin role (sync errors, consent reviews, tier application submissions) appear under **Needs attention**. When a group notification targets all Admins or all Coordinators of a team, **any recipient can resolve it for all** — the resolver's name is shown so no one duplicates work. Informational notifications (team changes, workspace credentials ready, drift fixes) fall under **Recent**. The bell icon shows a red count for actionable items and a green dot for informational-only.

### Configure Google sync

`/Google/SyncSettings` (Admin only) sets each Google service to **None**, **AddOnly**, or **AddAndRemove**. **None** is a fast kill switch — no redeploy — that turns off both the scheduled jobs (hourly team sync, daily 03:00 reconciliation) and manual Sync Now for that service.

The sync outbox (`/Google/SyncOutbox`) shows pending and failed events. From there you can **Requeue** a single failed event or **Requeue All Failed** to retry all permanently-failed events in bulk; both actions are audited. On a human's admin detail page (`/Users/Admin/{id}`), **Re-run Google Sync** enqueues sync events for all of that human's teams (Admin only, also audited).

See [GoogleIntegration](GoogleIntegration.md) for the full Google surface.

### Run technical operations (Admin only)

- **Configuration** (`/Debug/Configuration`) lists every auto-discovered setting, classified as critical, recommended, or optional, with sensitive values masked. The feedback API key is set here and enables the in-app feedback submission flow described in [Feedback](Feedback.md).
- **Logs** (`/Debug/Logs`) shows recent in-memory Serilog entries for quick triage without shelling in.
- **DbStats**, **CacheStats**, **DbVersion** report query statistics, cache hit/miss rates, and applied and pending EF migrations.
- **ClearHangfireLocks** removes stuck background-job locks; the app must be restarted afterwards to re-register recurring jobs.
- **Hangfire dashboard** (`/hangfire`) is Admin-only for inspecting and re-queueing jobs.

### Resolve duplicate accounts

`/Users/Admin/AccountMerges` is the single merge surface. It lists, in one queue, both user-submitted merge requests and auto-detected duplicate pairs (humans whose email addresses overlap across `User.Email` and `UserEmail.Email`, with Gmail / Googlemail equivalence). For any row you pick the survivor and merge; merging tombstones the archived account, re-FKs its data to the survivor, and consolidates all associated data onto the surviving account. You can also dismiss a request, or close an orphan request whose accounts were already merged.

### Purge (non-production only)

`/Users/Admin/{id}/Purge` permanently deletes a human and all associated data, severing the OAuth link so the next Google login creates a fresh account. Purge is **disabled in production**, and you cannot purge your own account.

## Related sections

- [Profiles](Profiles.md) — the humans list and per-human detail page are the Profiles admin surface.
- [Teams](Teams.md) — Teams Admin and Coordinator duties live here; system team sync is triggered from the Admin dashboard.
- [Google Integration](GoogleIntegration.md) — sync settings, workspace accounts, and sync audit views.
- [Feedback](Feedback.md) — Feedback Admin triages reports; all admins use the shared notification inbox.
- [Governance](Governance.md) — role assignments (Admin, Board, HumanAdmin, Coordinator roles) and tier application vote finalization.
