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
| ActorName | string | "SystemTeamSyncJob" or "Admin: Jane Doe" |
| RelatedEntityId | Guid? | Secondary entity |
| RelatedEntityType | string? | "User", "Team", etc. |

Table: `audit_log`

### Immutability

Database triggers prevent UPDATE and DELETE on the `audit_log` table, matching the pattern used for `consent_records`. The `ActorName` field preserves the actor's identity even if the user is later anonymized (FK uses `SetNull` on delete).

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
- `GoogleResourceAccessGranted` — (Phase 2) Google resource access granted
- `GoogleResourceAccessRevoked` — (Phase 2) Google resource access revoked

## Service Design

`IAuditLogService` provides two overloads:

1. **Job overload** — no human actor, accepts job name string
2. **Admin overload** — accepts actor user ID and display name

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

## Phase 2 (Future)

- Wire into `TeamService` (manual join/leave, approve/reject requests)
- Wire into `GoogleWorkspaceSyncService` (resource access grant/revoke)
- Per-resource audit view on team admin pages
- Global audit log page with filtering

## User Interface

### Per-User Audit View (MemberDetail page)

Displays the 50 most recent audit entries affecting a user, queried by:
- `EntityType = 'User' AND EntityId = @userId` (direct entries)
- `RelatedEntityId = @userId` (related entries, e.g., team membership changes)

Each entry shows:
- Description (bold)
- Badge: "System" (info) or "Admin" (secondary)
- Actor name
- Timestamp (right-aligned)

## Authorization

Audit log is visible only to Board and Admin roles (inherits from AdminController's `[Authorize(Roles = "Board,Admin")]`).

## Related Features

- [F-05: Membership Status](05-membership-status.md) — Suspension triggers audit entries
- [F-08: Background Jobs](08-background-jobs.md) — Jobs are primary audit producers
- [F-09: Administration](09-administration.md) — Admin actions produce audit entries
- [F-06: Teams](06-teams.md) — Team sync produces audit entries
