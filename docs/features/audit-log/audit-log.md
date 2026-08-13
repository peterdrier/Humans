<!-- freshness:triggers
  src/Humans.Application/Services/AuditLog/**
  src/Humans.Web/Controllers/AuditLogController.cs
  src/Humans.Web/Views/AuditLog/Index.cshtml
  src/Humans.Domain/Entities/AuditLogEntry.cs
  src/Humans.Infrastructure/Data/Configurations/AuditLog/**
-->
<!-- freshness:flag-on-change
  AuditLogEntry schema, AuditAction/GoogleSyncSource enum values, immutability triggers, and audit-log views/routes — review when AuditAction enum, AuditLogService, or audit-log UI change.
-->

# F-12: Audit Log

## Business Context

Background jobs and admin actions make changes on members' behalf (team enrollment, Google Drive/Group access, suspensions, account anonymization). The Board needs a structured, queryable audit trail showing what the system does automatically and what admins do manually, beyond what Serilog text logs provide.

## Data Model

### AuditLogEntry (append-only)

| Field | Type | Purpose |
|-------|------|---------|
| Id | Guid | PK |
| Action | AuditAction (string) | What happened |
| EntityType | string | "User", "Team", etc. |
| EntityId | Guid | Primary affected entity |
| Description | string | Human-readable text |
| OccurredAt | Instant | When |
| ActorUserId | Guid? | Human actor (null for jobs) |
| RelatedEntityId | Guid? | Secondary entity |
| RelatedEntityType | string? | "User", "Team", etc. |
| ResourceId | Guid? | Guid reference to GoogleResource, no DB-level FK constraint (Google sync only) |
| Success | bool? | Whether Google API call succeeded (Google sync only) |
| ErrorMessage | string? | Error details if call failed (Google sync only) |
| Role | string? | Role granted/revoked, e.g. "writer", "MEMBER" (Google sync only) |
| SyncSource | GoogleSyncSource? (string) | What triggered the sync action (Google sync only) |
| UserEmail | string? | Email at time of action, denormalized (Google sync only) |

Table: `audit_log`

### GoogleSyncSource Enum

Stored as string in the database. Values:
- `TeamMemberJoined` — User joined a team, triggering resource access
- `TeamMemberLeft` — User left a team, triggering revocation
- `ManualSync` — Admin clicked "Sync Now"
- `ScheduledSync` — Automated periodic sync
- `Suspension` — Member suspension triggered revocation
- `SystemTeamSync` — System team sync job

### Immutability

Database triggers prevent UPDATE and DELETE on the `audit_log` table, matching the pattern used for `consent_records`. `ActorUserId` is a bare cross-section Guid column with no DB-level FK constraint (nobodies-collective/Humans#992) — a deleted/anonymized actor is handled at the application layer: `AuditViewerService` looks up the actor by id and falls back to displaying "Someone" when no match is found.

### AuditAction Enum

Stored as string in the database. New values can be appended without migration.

The full value catalog lives in [`src/Sections/Humans.AuditLog/Docs/AuditLog.md`](../../../src/Sections/Humans.AuditLog/Docs/AuditLog.md) and is regenerated from `src/Humans.Domain/Enums/AuditAction.cs` by `/freshness-sweep`. It is not duplicated here — one source of truth.

## Service Design

The section splits into a write side and a read+render side:

**`IAuditLogService` (write)** provides two `LogAsync` overloads:

1. **Job overload** — no human actor, accepts job name (prefixed to description)
2. **Human overload** — accepts actor user ID

Each call is self-persisting via `IAuditLogRepository.AddAsync`, which opens a fresh `DbContext` via `IDbContextFactory<HumansDbContext>` and saves immediately (design-rules §7a). Callers do not need to flush audit, and audit does not roll back if a later business step fails. `LogGoogleSyncAsync` is the third overload for permission-change events with the Google-specific nullable fields.

**`IAuditViewerService` (read+render)** owns the read path. It returns resolved `AuditEvent` records — actor/subject/target-team display names are batch-resolved inside the section (no per-call-site dance with `GetUserDisplayNamesAsync` / `GetTeamNamesAsync`). Overloads cover the global `/AuditLog` page, per-entity history (Profile/Team/Calendar/etc.), per-user history (MemberDetail), and the agent's `get_audit_history` tool. Verb tables (`GetActionVerb`, `GetActionSelfVerb`, `ShouldRenderDescriptionTail`) live in the stateless `AuditEventTextualizer` helper, which backs both `AuditEvent.RenderPlainText` (agent tool output, with viewer-GUID → "You" substitution) and `RenderStructured` (the view-component HTML composition path).

`IAuditLogService` no longer exposes display-name lookups to controllers — `GetUserDisplayNamesAsync` / `GetTeamNamesAsync` are reached only through `IAuditViewerService`.

## Phase 1 Coverage (Current)

### Background Jobs

| Job | Actions Logged |
|-----|---------------|
| SystemTeamSyncJob | TeamMemberAdded, TeamMemberRemoved |
| SuspendNonCompliantMembersJob | MemberSuspended |
| ProcessAccountDeletionsJob | AccountAnonymized |

### Admin Actions (AdminController)

| Action | AuditAction |
|--------|-------------|
| Suspend member | MemberSuspended |
| Unsuspend member | MemberUnsuspended |
| Approve volunteer | VolunteerApproved |
| Add role | RoleAssigned |
| End role | RoleEnded |

## Phase 2 Coverage (Current)

### TeamService

| Action | AuditAction | Actor |
|--------|-------------|-------|
| User joins open team | TeamJoinedDirectly | User |
| User leaves team | TeamLeft | User |
| Join request approved | TeamJoinRequestApproved | Approver (Board/Coordinator) |
| Join request rejected | TeamJoinRequestRejected | Approver (Board/Coordinator) |
| Member removed by admin | TeamMemberRemoved | Admin (Board/Coordinator) |
| Member role changed | TeamMemberRoleChanged | Admin (Board/Coordinator) |

### GoogleWorkspaceSyncService

| Action | AuditAction | Actor |
|--------|-------------|-------|
| Drive folder provisioned | GoogleResourceProvisioned | GoogleWorkspaceSyncService |
| Google Group provisioned | GoogleResourceProvisioned | GoogleWorkspaceSyncService |
| User added to Group | GoogleResourceAccessGranted | GoogleWorkspaceSyncService |
| User removed from Group | GoogleResourceAccessRevoked | GoogleWorkspaceSyncService |
| Drive permission added (direct or sync) | GoogleResourceAccessGranted | GoogleWorkspaceSyncService |
| Drive permission removed (direct or sync) | GoogleResourceAccessRevoked | GoogleWorkspaceSyncService |

### DriveActivityMonitorJob

| Action | AuditAction | Actor |
|--------|-------------|-------|
| Anomalous permission change detected | AnomalousPermissionDetected | DriveActivityMonitorJob |

### Google Sync Audit (LogGoogleSyncAsync)

For permission-change actions, `LogGoogleSyncAsync` populates the Google-specific nullable fields alongside the standard fields. This provides structured detail for per-resource and per-user audit views without requiring a separate table.

The service method sets `EntityType = "GoogleResource"` and `EntityId = resourceId` for these entries.

## User Interface

### Global Audit Log Page (`/AuditLog`)

Accessible to Board and Admin. Displays all audit log entries with filtering by action type. Features:
- Filter buttons: All, Anomalous Permissions, Access Granted/Revoked, Suspensions, Roles
- Anomalous entries highlighted with warning styling
- Alert banner showing total anomaly count
- Paginated (50 per page)

### Drive Activity Check (`/AuditLog/CheckDriveActivity`)

POST action on `AuditLogController`. Manual trigger for the Drive Activity monitor. Redirects to `/AuditLog?filter=AnomalousPermissionDetected` after completion.

### Per-Resource Google Sync Audit (`/AuditLog/Resource/{id}`)

Displays all audit entries for a specific Google resource, queried by `ResourceId`. Shows structured Google sync details: user email, role, sync source, success/failure status, and error messages. Accessible to Board and Admin. Accessed via "Audit" button on each row of the Google Sync page.

### Per-User Google Sync Audit (`/AuditLog/Human/{id}`)

Displays all Google sync audit entries affecting a specific user, queried by `RelatedEntityId = userId` where `ResourceId IS NOT NULL`. Includes the Google resource name, resolved via `ITeamResourceService.GetResourceNamesByIdsAsync` (no navigation property — cross-section read-interface call). Accessible to HumanAdmin and Admin. Accessed via the Member Detail page sidebar.

### Per-User Audit View (MemberDetail page)

Displays the 50 most recent audit entries affecting a user, queried by:
- `EntityType = 'User' AND EntityId = @userId` (direct entries)
- `RelatedEntityId = @userId` (related entries, e.g., team membership changes)

Each entry renders structurally as: `[timestamp] — [actor] [verb] [subject] in [team] — [description]`. The verb tables (`GetActionVerb` / `GetActionSelfVerb`) live in `AuditEventTextualizer`; the self-form is used when actor == subject to avoid dangling prepositions. `IAuditViewerService` resolves the page, batch-loading actor/subject/target-team display names inside the section and returning `AuditEvent` records; the shared `AuditLogViewComponent` then composes HTML around `AuditEvent.RenderStructured`, which produces the field bundle (actor/subject as clickable `<human-link>` data, verb, description tail). Unmapped actions fall back to `[actor] · [ActionName] · [subject] — [description]` so attribution is never lost.

### Agent tool — `get_audit_history`

Agent dispatcher tool that returns the calling user's audit history as plain text. Default 20 lines, hard-capped at 50 (limit clamped server-side; minimum 1). Empty result returns "No audit history for this user." Each line is the `RenderPlainText` of an `AuditEvent`, with the calling user's GUID rewritten to "You" so the agent never echoes the user's id. Used by the system prompt for personal-history questions ("who voluntold me?", "when did I get added to the Build team?", role changes, approvals).

## Authorization

The global audit log (`/AuditLog`) and per-resource/per-user views are visible only to Board and Admin (or HumanAdmin for the per-user view) — they live on `AuditLogController` with the appropriate policies. The Finance audit log (`/Finance/AuditLog`) is restricted to FinanceAdmin and Admin.

## Related Features

- [F-05: Volunteer Status](../onboarding/volunteer-status.md) — Suspension triggers audit entries
- [F-08: Background Jobs](../global/background-jobs.md) — Jobs are primary audit producers
- [F-09: Administration](../global/administration.md) — Admin actions produce audit entries
- [F-06: Teams](../teams/teams.md) — Team sync produces audit entries
- [F-13: Drive Activity Monitoring](../google-integration/drive-activity-monitoring.md) — Anomalous permission detection
