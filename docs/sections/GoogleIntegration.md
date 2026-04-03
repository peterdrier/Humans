# Google Integration — Section Invariants

## Controller Structure

All Google integration management is consolidated in `GoogleController` (`/Google`), with team-level resource linking remaining in `TeamAdminController`. Key routes:

- `/Google` — Google integration dashboard
- `/Google/SyncSettings` — per-service sync mode management
- `/Google/Sync` — resource sync status dashboard (preview/execute)
- `/Google/AllGroups` — domain-wide group management
- `/Google/Accounts` — @nobodies.team Workspace account management
- `/Google/Human/{id}/SyncAudit` — per-human sync audit
- `/Google/Sync/Resource/{id}/Audit` — per-resource sync audit
- `/Google/CheckGroupSettings`, `/Google/CheckEmailMismatches` — diagnostic tools

Team-level resource linking stays at `/Teams/{slug}/Resources` in `TeamAdminController`.

## Concepts

- **Google Resources** are Shared Drive folders, Shared Drives, Drive files, and Google Groups linked to a team. When a human joins or leaves a team, their access to the team's linked Google resources is automatically managed.
- **Sync Mode** controls how the system interacts with Google APIs for each service type. Modes are: None (disabled), AddOnly (grant access but never revoke), or AddAndRemove (full bidirectional sync).
- **Reconciliation** compares the expected Google resource state (based on team membership) against the actual Google resource state, detecting drift.
- The **sync outbox** queues resource-level sync events for processing by a background job.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Admin | Manage sync settings (per-service mode). Trigger manual syncs and execute sync actions. View reconciliation results. Check and remediate Google Group settings drift. Link unlinked groups to teams. Review and apply email backfill corrections. Manage @nobodies.team Workspace accounts (provision, suspend, reactivate, reset password, link). Provision per-human @nobodies.team email |
| TeamsAdmin, Board, Admin | View resource sync status dashboard. Link and unlink Google resources (Drive folders, Groups) to teams via TeamAdminController. View resource status. Trigger per-resource sync |
| Coordinator | Link and unlink Google resources for their own department (via TeamAdminController). Trigger per-resource sync for their own department |
| Board, Admin | View Drive activity anomaly check results. View sync audit logs |
| HumanAdmin, Board, Admin | View per-human Google sync audit |
| Background jobs | Automated sync: system team sync (hourly), resource reconciliation (daily at 03:00), sync outbox processing, resource provisioning |

## Invariants

- All Google Drive resources are on Shared Drives. The system does not use regular (My Drive) folders.
- Only direct permissions are managed by the system. Inherited Shared Drive permissions are excluded from drift detection and sync.
- Drive folders with `RestrictInheritedAccess = true` have `inheritedPermissionsDisabled` enforced by the reconciliation job. Drift (manual re-enablement of inheritance) is detected and corrected automatically, with an audit trail entry.
- Sync settings are per-service (Google Drive, Google Groups, Discord). Setting a service to None disables sync without redeploying.
- A human's Google service email is their @nobodies.team email if provisioned, otherwise their OAuth login email.
- Each human has a `GoogleEmailStatus` (`Unknown`, `Valid`, `Rejected`). When Google permanently rejects an email (HTTP 400/403/404), the status is set to `Rejected` and new outbox events are not enqueued for that human. When a human changes their Google email, the status resets to `Unknown` and fresh sync events are enqueued.
- Permanent Google API errors (HTTP 400, 403, 404) mark outbox events as `FailedPermanently` and stop retrying immediately. Transient errors (5xx, 429, etc.) continue retrying up to the configured limit.
- The system authenticates to Google APIs as a service account — no domain-wide delegation or user impersonation.
- There are exactly four gateway operations that can modify Google access, and all enforce the current sync mode before executing.

## Negative Access Rules

- TeamsAdmin and Board **cannot** manage sync settings — that is Admin-only.
- Coordinators **cannot** manage sync settings, execute bulk sync actions, or remediate Google Group settings drift.
- Regular humans have no access to Google resource management.

## Triggers

- When team membership changes, sync outbox events are queued for Google Group and Drive updates.
- When a human's Google email changes, `GoogleEmailStatus` resets to `Unknown` and fresh sync events are enqueued for all current team memberships.
- When a Google resource is linked to a team, current team members are synced to that resource.
- When a Google resource is unlinked, managed permissions are removed (if sync mode allows).
- The system team sync job runs hourly, reconciling system team membership.
- The reconciliation job runs daily at 03:00, detecting drift between expected and actual Google resource state.

## Cross-Section Dependencies

- **Teams**: Google resources are linked per team. Team membership drives Google Group and Drive access.
- **Profiles**: A human's Google service email determines the email address used for Google Groups and Drive access.
- **Admin**: Sync settings management is Admin-only.
- **Onboarding**: Volunteer activation triggers system team sync, which cascades to Google Group membership.
