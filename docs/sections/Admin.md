# Admin — Section Invariants

## Concepts

- **System administration** covers infrastructure-level operations: email outbox, background jobs, configuration, logs, and database health. Google integration tools have been consolidated into the Google section (`/Google`).
- **Human administration** covers person-level operations: viewing the humans list, managing role assignments, suspending/unsuspending, tier management, and account merging.
- **Purge** permanently deletes a human and all associated data. Only available in non-production environments.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Admin | Full system administration: email outbox (pause/resume/retry/discard), Hangfire dashboard, configuration review, in-memory logs, database version, clear Hangfire locks, email preview, purge humans. Google integration tools are consolidated at `/Google` |
| HumanAdmin, Board, Admin | Human administration: view humans list with admin detail, view/edit role assignments, manage tier, suspend/unsuspend, view audit log and email outbox per human |
| Board, Admin | Legal document management (create/edit documents and versions) |

## Invariants

- Admin can assign all roles (including Admin). Board and HumanAdmin can assign all roles except Admin.
- Configuration settings are auto-discovered via `ConfigurationRegistry` — any setting accessed through the `GetRequiredSetting`/`GetOptionalSetting` extension methods is automatically surfaced on the Configuration page. Settings are classified as critical (app won't function), recommended (feature degrades), or optional. Non-sensitive values display in full; sensitive values are masked.
- The email outbox can be paused and resumed. While paused, no outgoing emails are processed.
- Individual failed emails can be retried (re-queued) or discarded (permanently deleted).
- Sync settings control per-service Google sync behavior (None / AddOnly / AddAndRemove). Setting a service to None disables sync without redeploying.
- Purging a human permanently deletes the account and all associated data, including severing the OAuth link so the next Google login creates a fresh account.
- User merge consolidates two accounts into one, transferring all associated data to the surviving account.
- Duplicate account detection scans for email addresses appearing on multiple accounts (across User.Email and UserEmail.Email, with gmail/googlemail equivalence). Admin can resolve by archiving the duplicate and re-linking its logins to the real account.
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
