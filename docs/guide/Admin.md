# Admin

## What this section is for

The Admin section is the global control panel: managing humans (suspending, approving, assigning roles, purging in non-production), configuring Google sync, reading the audit log, triaging the notification inbox, and running technical operations like configuration review, in-memory logs, database version, cache and query stats, and Hangfire lock cleanup.

Admin is layered. **Board** and **HumanAdmin** can do human management — the list, detail, role assignments, suspend/unsuspend, approve, reject. **Admin** is the superset and additionally owns technical operations, sync settings, duplicate-account resolution, and workspace-account provisioning. Domain admins like Teams Admin, Camp Admin, and Ticket Admin are separate roles covered in their own section guides.

![TODO: screenshot — Admin dashboard home showing humans summary, recent audit entries, and sync status]

## Key pages at a glance

- `/Admin` — Admin dashboard with counts, recent activity, and quick actions.
- `/Board` — Board dashboard with scoped counts and a link to the audit log.
- `/Profile/Admin` — humans list; `?filter=pending` scopes to pending approvals.
- `/Profile/{id}/Admin` — per-human detail, with suspend, unsuspend, approve, reject, add role, end role.
- `/Profile/{id}/Admin/Outbox` — per-human email outbox.
- `/Board/AuditLog` — global audit log, filterable and paginated.
- `/Notifications` — your notification inbox.
- `/Google/SyncSettings` — per-service sync mode (Admin only).
- `/Admin/Configuration`, `/Admin/Logs`, `/Admin/DbStats`, `/Admin/CacheStats`, `/Admin/DbVersion` — technical diagnostics.
- `/Admin/ClearHangfireLocks` — clear stuck job locks (Admin only; requires restart).
- `/Admin/DuplicateAccounts` and `/Admin/MergeRequests` — duplicate detection and merge flow.
- `/Admin/Humans/{id}/Purge` — permanent delete, disabled in production.
- `/hangfire` — Hangfire dashboard, Admin only.

## As a Volunteer

Admin pages are not visible to you.

## As a Coordinator

The Coordinator role does not include global admin access; the domain-specific admin roles (Teams Admin, Camp Admin, Ticket Admin, Finance Admin, Feedback Admin, and so on) are separate and covered in their respective section guides.

## As a Board member / Admin

### Work the dashboard

Open `/Admin` (or `/Board` if you are Board but not Admin). The counts at the top — Total Humans, Active, Pending Volunteers, Pending Applications, Pending Consents — double as entry points. The recent-activity feed shows the latest audit entries so you can see what the system and other admins have been doing.

### Manage humans

Open `/Profile/Admin`. Search by name or email, filter pending volunteers with `?filter=pending`, and click through to a human's detail page. From there you can:

- **Approve** a pending volunteer. Approval, combined with all required consents signed, immediately adds the human to the Volunteers team and grants the ActiveMember claim — no wait for the hourly sync.
- **Reject** a signup. Audited, and the human is notified.
- **Suspend** or **Unsuspend**. Suspension requires a note; it revokes Google Workspace access on the next sync and ends current memberships. Unsuspension clears the flag and re-queues access.
- **Add role** or **End role**. Role assignments are temporal — valid-from plus optional valid-to — and every change is audited. **Admin** can assign any role. **Board** and **HumanAdmin** can assign any role **except** Admin. The first Admin must be seeded directly in the database.

Every human-admin action writes an audit entry with your user as the actor.

### Read the audit log

`/Board/AuditLog` shows every audit entry — role changes, suspensions, team join decisions, Google sync events, tier application decisions, anomalous Drive permission changes, workspace-account lifecycle, and more. Filter buttons scope to Anomalous Permissions, Access Granted/Revoked, Suspensions, and Roles. The log is **append-only** — database triggers prevent update and delete — and deleted humans show as "Deleted User" rather than disappearing from the trail. Per-human and per-resource audit views are also linked from the human and resource detail pages.

### Triage the notification inbox

`/Notifications` is your shared "what needs my attention" view. Actionable notifications targeted at the Admin role (sync errors, consent reviews, tier application submissions) appear under **Needs attention**. When a group notification targets all Admins or all Coordinators of a team, **any recipient can resolve it for all** — the resolver's name is shown so no one duplicates work. Informational notifications (team changes, workspace credentials ready, drift fixes) fall under **Recent**. The bell icon shows a red count for actionable items and a green dot for informational-only.

### Review sent notifications

`/Admin/Notifications/Sent` is the audit view of recent system and admin-generated notifications that have been delivered to humans. Use it to confirm delivery of a specific message, check what humans are actually seeing in their inbox, and investigate reports about unexpected emails or missing notifications. Each entry shows the recipient, channel, category, and delivery status.

### Configure Google sync

`/Google/SyncSettings` (Admin only) sets each Google service to **None**, **AddOnly**, or **AddAndRemove**. **None** is a fast kill switch — no redeploy — that turns off both the scheduled jobs (hourly team sync, daily 03:00 reconciliation) and manual Sync Now for that service. See [GoogleIntegration](GoogleIntegration.md) for the full Google surface.

### Run technical operations (Admin only)

- **Configuration** (`/Admin/Configuration`) lists every auto-discovered setting, classified as critical, recommended, or optional, with sensitive values masked. The feedback API key is set here and enables the in-app feedback submission flow described in [Feedback](Feedback.md).
- **Logs** (`/Admin/Logs`) shows recent in-memory Serilog entries for quick triage without shelling in.
- **DbStats**, **CacheStats**, **DbVersion** report query statistics, cache hit/miss rates, and applied and pending EF migrations.
- **ClearHangfireLocks** removes stuck background-job locks; the app must be restarted afterwards to re-register recurring jobs.
- **Hangfire dashboard** (`/hangfire`) is Admin-only for inspecting and re-queueing jobs.

### Resolve duplicate accounts

`/Admin/DuplicateAccounts` scans for humans whose email addresses overlap across `User.Email` and `UserEmail.Email`, with Gmail / Googlemail equivalence. Review a candidate, then archive the duplicate and re-link its logins. Pending merge requests live at `/Admin/MergeRequests`; accepting one consolidates all associated data onto the surviving account.

### Purge (non-production only)

`/Admin/Humans/{id}/Purge` permanently deletes a human and all associated data, severing the OAuth link so the next Google login creates a fresh account. Purge is **disabled in production**, and you cannot purge your own account.

## Related sections

- [Profiles](Profiles.md) — the humans list and per-human detail page are the Profiles admin surface.
- [Teams](Teams.md) — Teams Admin and Coordinator duties live here; system team sync is triggered from the Admin dashboard.
- [Google Integration](GoogleIntegration.md) — sync settings, workspace accounts, and sync audit views.
- [Feedback](Feedback.md) — Feedback Admin triages reports; all admins use the shared notification inbox.
- [Governance](Governance.md) — role assignments (Admin, Board, HumanAdmin, Coordinator roles) and tier application vote finalization.
