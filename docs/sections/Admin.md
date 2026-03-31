# Admin — Section Invariants

## Concepts

- **System administration** covers infrastructure-level operations: sync settings, email outbox, background jobs, configuration, logs, Google group management, and database health.
- **Human administration** covers person-level operations: viewing the humans list, managing role assignments, provisioning workspace accounts, suspending/unsuspending, tier management, and account merging.
- **Purge** permanently deletes a human and all associated data. Only available in non-production environments.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Admin | Full system administration: sync settings, email outbox (pause/resume/retry/discard), Hangfire dashboard, configuration review, in-memory logs, Google group management (check settings, remediate drift, link groups to teams), system team sync, database version, clear Hangfire locks, email preview, email backfill, purge humans |
| HumanAdmin, Board, Admin | Human administration: view humans list with admin detail, view/edit role assignments, provision @nobodies.team email accounts, manage tier, suspend/unsuspend, view audit log and email outbox per human |
| Board, Admin | Legal document management (create/edit documents and versions). Email administration (workspace account management) |
| HumanAdmin, Admin | Provision @nobodies.team workspace email accounts |

## Invariants

- Admin can assign all roles (including Admin). Board and HumanAdmin can assign all roles except Admin.
- The email outbox can be paused and resumed. While paused, no outgoing emails are processed.
- Individual failed emails can be retried (re-queued) or discarded (permanently deleted).
- Sync settings control per-service Google sync behavior (None / AddOnly / AddAndRemove). Setting a service to None disables sync without redeploying.
- Purging a human permanently deletes the account and all associated data, including severing the OAuth link so the next Google login creates a fresh account.
- User merge consolidates two accounts into one, transferring all associated data to the surviving account.
- Hangfire locks can be cleared if background jobs are stuck; the application must be restarted afterward to re-register recurring jobs.

## Negative Access Rules

- HumanAdmin **cannot** access system administration (sync settings, email outbox, logs, Hangfire, configuration, Google group management, purge).
- Board **cannot** assign the Admin role.
- HumanAdmin **cannot** assign the Admin role.
- Board **cannot** purge humans, manage sync settings, manage the email outbox, or access the Hangfire dashboard.
- No one can purge their own account.
- Purge is disabled in production environments.

## Triggers

- When sync settings are changed, sync jobs respect the new mode on next execution.
- When the email outbox is paused, outgoing email processing stops until resumed.
- When a human is purged, all associated data is cascade-deleted.

## Cross-Section Dependencies

- **Google Integration**: Sync settings, group management, and reconciliation are administered here.
- **Email**: Outbox pause/resume and retry/discard operations.
- **Legal & Consent**: Document version management is administered by Board and Admin.
- **Governance**: Role assignment management via human admin actions.
- **All sections**: Admin has override access to all areas of the system.
