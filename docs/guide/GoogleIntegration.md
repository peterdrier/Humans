<!-- freshness:triggers
  src/Humans.Web/Views/Google/**
  src/Humans.Web/Views/TeamAdmin/Resources.cshtml
  src/Humans.Web/Controllers/GoogleController.cs
  src/Humans.Application/Services/GoogleIntegration/**
  src/Humans.Application/Services/Teams/TeamResourceService.cs
  src/Humans.Domain/Entities/GoogleResource.cs
  src/Humans.Domain/Entities/GoogleSyncOutboxEvent.cs
  src/Humans.Domain/Entities/SyncServiceSettings.cs
  src/Humans.Domain/Constants/GoogleSyncOutboxEventTypes.cs
  src/Humans.Infrastructure/Data/Configurations/GoogleResourceConfiguration.cs
  src/Humans.Infrastructure/Data/Configurations/GoogleSyncOutboxEventConfiguration.cs
  src/Humans.Infrastructure/Data/Configurations/SyncServiceSettingsConfiguration.cs
-->
<!-- freshness:flag-on-change
  Sync mode plumbing, drift detection, workspace account provisioning, Drive activity monitor, and team-resource linking. Review when Google services, sync settings, or related entities change.
-->

# Google Integration

## What this section is for

Google Integration wires teams up to Google Workspace. When you join a team, the app grants you access to that team's **Google Group** and any linked **[Shared Drive](Glossary.md#shared-drive) folders or files**; leaving revokes it. [Admins](Glossary.md#admin) can also provision a `@nobodies.team` **Workspace account** for any human, and the app monitors Drive folders for permission changes made outside the system.

This section covers the sync mechanics, drift detection, and admin tools. For the human-facing side of email — using your `@nobodies.team` mailbox, sending from a group address, the inbox setup — see [Email](Email.md).

All Drive resources are on **Shared Drives only** (no personal My Drive), and the app manages only **direct** permissions — inherited Shared Drive permissions are left alone. Sync is **mode-gated per service**: an Admin must switch Google Drive and Google Groups from `None` to `AddOnly` or `AddAndRemove` before any job or manual sync will modify Google. See [sync mode](Glossary.md#sync-mode) and [reconciliation](Glossary.md#reconciliation) for definitions.

## Key pages at a glance

- **Google dashboard** (`/Google`) — entry point; buttons for Check Group Settings, Check Email Mismatches, and links to the sub-pages.
- **Sync status** (`/Google/Sync`) — tabbed Drive / Groups view of every active linked resource with drift and Sync Now.
- **Sync settings** (`/Google/SyncSettings`) — per-service sync mode (None / AddOnly / AddAndRemove).
- **All Groups** (`/Google/AllGroups`) — every Google Group in the domain, linked or not.
- **Workspace accounts** (`/Google/Accounts`) — every `@nobodies.team` account, with link-to-human search.
- **Sync audit** — per-human (`/Google/Human/{id}/SyncAudit`) and per-resource (`/Google/Sync/Resource/{id}/Audit`).
- **Team resources** (`/Teams/{slug}/Admin/Resources`) — where coordinators link, unlink, and sync a team's Drive folders, files, and Groups.
- **Drive activity** — anomalous changes land in `/Board/AuditLog` under the "Anomalous Permissions" filter.

## As a [Volunteer](Glossary.md#volunteer)

### Why you may have a `@nobodies.team` account

An admin can provision a `@nobodies.team` account for you. You get a credentials email at your **personal** address — username, temporary password, sign-in link — and on first login you must change the password and set up 2FA. That address then becomes your **Google service email** (the app's [service account](Glossary.md#service-account) uses it to add you to Groups and Shared Drive folders for every team). Until provisioning happens, your OAuth login email is used instead.

For how to use the mailbox itself, set up two-factor auth, and send "as" your team's group address, see [Email](Email.md).

### Set or change your Google service email

Go to **Profile → Emails** and verify the address you want Google to know about. Changing it resets your sync status to `Unknown` and re-queues access for every team. If Google permanently rejects an address, your status is set to `Rejected` and the app stops trying — fix it on the Emails page to reset.

![TODO: screenshot — Profile Emails page showing the verified Google service email with notification-target toggle]

### What access you get automatically

Joining a team grants you its Group membership and writer access to its linked Shared Drive folders and files on the next sync; leaving revokes it. Sub-team members also get parent-department resources — rolled up one way (department members do not get sub-team resources).

## As a [Coordinator](Glossary.md#coordinator)

(assumes Volunteer knowledge)

### Link a Google resource to your department

Open `/Teams/{slug}/Admin/Resources`. Three link forms:

- **Link Drive folder** — paste a Shared Drive folder URL; the folder must already be shared with the app's service account as Editor (the page shows that email if validation fails).
- **Link Drive file** — paste a Sheet, Doc, Slides, or Forms URL. Same sharing requirement.
- **Link Google Group** — paste the group email; the service account must be a Group Manager.

Duplicate links on the same team are rejected. Unlinking is soft — the record flips to inactive and nothing is deleted in Google. Each linked resource has a **Sync** button that runs just that resource, respecting the current sync mode — use it to propagate a membership change immediately instead of waiting for the next job.

### What coordinators cannot do

Sync settings, group-settings drift remediation, and workspace account provisioning are Admin-only. You also cannot manage resources for teams you do not coordinate. The `TeamResourceManagement:AllowLeadsToManageResources` config flag gates coordinator access entirely — if it is off, only Board can link and unlink.

## As a Board member / Admin

(assumes Coordinator knowledge)

### Configure sync modes

At `/Google/SyncSettings` (Admin only), each service (Google Drive, Google Groups) gets a mode: **None** (jobs and manual sync skip the service — default on fresh installs), **AddOnly** (grants missing access, never revokes), or **AddAndRemove** (full bidirectional sync). Flipping a service to `None` is a fast kill switch with no redeploy.

### Review sync status and run reconciliation

At `/Google/Sync` (TeamsAdmin, Board, Admin can view; Admin-only to execute) the Drive and Groups tabs show Total / In Sync / Drifted / Errors cards and per-resource rows with members to add or remove. **Sync Now** runs one resource, **Sync All** the tab. The nightly `GoogleResourceReconciliationJob` (03:00) and hourly `SystemTeamSyncJob` do this automatically, mode-gated.

### Manage `@nobodies.team` accounts

At `/Google/Accounts`, list every workspace account, search humans by name, and link orphaned accounts. To provision a new account, open `/Human/{id}/Admin` and use **Provision Email** — the app creates the Google account, sets a temporary password, sends credentials to the human's personal email, and auto-links the new address as the Google service email.

### Check group settings and email mismatches

From `/Google`, **Check Group Settings** lists every group whose settings (who-can-post, membership visibility, external members, archive) have drifted from expected values; the nightly reconciliation job also remediates drift automatically when Groups sync is not `None`. **Check Email Mismatches** surfaces humans whose Google service email does not match what Google has on file.

### Monitor Drive activity

The hourly `DriveActivityMonitorJob` queries the Drive Activity API for permission changes made by anyone other than the service account and logs each as `AnomalousPermissionDetected`. Review at `/Board/AuditLog` with the **Anomalous Permissions** filter; you can also trigger a check on demand from that page.

### Sync audit

`/Google/Human/{id}/SyncAudit` shows every sync event for a given human — useful when access is missing. `/Google/Sync/Resource/{id}/Audit` does the same for one resource.

## Related sections

- [Email](Email.md) — the human-facing side of `@nobodies.team` mailboxes and group addresses.
- [Teams](Teams.md) — membership drives Group and Drive access; resource linking lives on the team admin page.
- [Profiles](Profiles.md) — set your Google service email on the Emails tab.
- [Glossary](Glossary.md) — service account, Shared Drive, sync mode, reconciliation.
