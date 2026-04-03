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
| ResourceId | Guid? | FK to GoogleResource (Google sync only) |
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

Database triggers prevent UPDATE and DELETE on the `audit_log` table, matching the pattern used for `consent_records`. The `ActorUserId` FK uses `SetNull` on delete, so deleted/anonymized users show as "Deleted User" in the UI.

### AuditAction Enum

Stored as string in the database. New values can be appended without migration.

- `TeamMemberAdded` — System sync added a user to a team
- `TeamMemberRemoved` — System sync removed a user from a team
- `MemberSuspended` — Admin or system suspended a member
- `MemberUnsuspended` — Admin unsuspended a member
- `AccountAnonymized` — Account deletion job anonymized a user
- `RoleAssigned` — Admin assigned a governance role
- `RoleEnded` — Admin ended a governance role
- `VolunteerApproved` — Admin approved a volunteer
- `GoogleResourceAccessGranted` — Google resource access granted (Group or Drive folder)
- `GoogleResourceAccessRevoked` — Google resource access revoked (Group or Drive folder)
- `GoogleResourceProvisioned` — New Google resource created (Drive folder or Group)
- `TeamJoinedDirectly` — User joined an open team directly
- `TeamLeft` — User left a team voluntarily
- `TeamJoinRequestApproved` — Approver approved a team join request
- `TeamJoinRequestRejected` — Approver rejected a team join request
- `TeamMemberRoleChanged` — Member role changed within a team
- `AnomalousPermissionDetected` — Drive Activity API detected a permission change not made by the system

## Service Design

`IAuditLogService` provides two overloads:

1. **Job overload** — no human actor, accepts job name (prefixed to description)
2. **Human overload** — accepts actor user ID

The service adds entries to the `DbContext` without calling `SaveChangesAsync`. Entries are saved atomically with the caller's business operation.

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

### Global Audit Log Page (`/Admin/AuditLog`)

Displays all audit log entries with filtering by action type. Features:
- Filter buttons: All, Anomalous Permissions, Access Granted/Revoked, Suspensions, Roles
- Anomalous entries highlighted with warning styling
- Alert banner showing total anomaly count
- "Check Drive Activity Now" button for manual trigger
- Paginated (50 per page)

### Per-Resource Google Sync Audit (`/Admin/GoogleSync/Resource/{id}/Audit`)

Displays all audit entries for a specific Google resource, queried by `ResourceId`. Shows structured Google sync details: user email, role, sync source, success/failure status, and error messages. Accessed via "Audit" button on each row of the Google Sync page.

### Per-User Google Sync Audit (`/Admin/Humans/{id}/GoogleSyncAudit`)

Displays all Google sync audit entries affecting a specific user, queried by `RelatedEntityId = userId` where `ResourceId IS NOT NULL`. Includes the Google resource name via navigation property. Accessed via "View Google Sync Audit" button on the Member Detail page sidebar.

### Per-User Audit View (MemberDetail page)

Displays the 50 most recent audit entries affecting a user, queried by:
- `EntityType = 'User' AND EntityId = @userId` (direct entries)
- `RelatedEntityId = @userId` (related entries, e.g., team membership changes)

Each entry shows:
- Description (bold)
- Badge: "System" (info) for job-generated entries, "Admin" (secondary) for human-initiated entries
- Actor name: resolved at render time from ActorUserId via batch lookup
- Timestamp (right-aligned)

## Authorization

Audit log is visible only to Board and Admin roles (inherits from AdminController's `[Authorize(Roles = "Board,Admin")]`).

## Related Features

- [F-05: Volunteer Status](05-volunteer-status.md) — Suspension triggers audit entries
- [F-08: Background Jobs](08-background-jobs.md) — Jobs are primary audit producers
- [F-09: Administration](09-administration.md) — Admin actions produce audit entries
- [F-06: Teams](06-teams.md) — Team sync produces audit entries
- [F-13: Drive Activity Monitoring](13-drive-activity-monitoring.md) — Anomalous permission detection
