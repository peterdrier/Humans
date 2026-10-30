<!-- freshness:triggers
  src/Sections/Humans.AuditLog/Services/**
  src/Sections/Humans.AuditLog/Contracts/**
  src/Sections/Humans.AuditLog/ViewComponents/**
  src/Sections/Humans.AuditLog/Controllers/AuditLogController.cs
  src/Sections/Humans.AuditLog/Views/AuditLog/Index.cshtml
  src/Sections/Humans.AuditLog/Domain/AuditLogEntry.cs
  src/Sections/Humans.AuditLog/Data/Configurations/**
-->
<!-- freshness:flag-on-change
  AuditLogEntry schema, audit action definitions, immutability triggers, and audit-log views/routes — review when AuditActionDefinition, AuditLogService, or audit-log UI change.
-->

# F-12: Audit Log

## Business Context

Background jobs and admin actions make changes on members' behalf (team enrollment, Google Drive/Group access, suspensions, account anonymization). The Board needs a structured, queryable audit trail showing what the system does automatically and what admins do manually, beyond what Serilog text logs provide.

## Data Model

### AuditLogEntry (append-only)

| Field | Type | Purpose |
|-------|------|---------|
| Id | Guid | PK |
| Action | string | What happened: an `AuditActionDefinition.Name` |
| EntityType | string | "User", "Team", etc. |
| EntityId | Guid | Primary affected entity |
| Description | string | Human-readable text |
| OccurredAt | Instant | When |
| ActorUserId | Guid? | Human actor (null for jobs) |
| RelatedEntityId | Guid? | Secondary entity |
| RelatedEntityType | string? | "User", "Team", etc. |

Table: `audit_log`

The columns `ResourceId`, `Success`, `ErrorMessage`, `Role`, `SyncSource` and `UserEmail` were the Google-sync wing. GoogleIntegration's own `google_sync_log` replaced them in nobodies-collective/Humans#1083; nothing reads or writes them any more and the entity keeps them only so the historical rows stay readable until the data move retires them. The column drop is its own PR under `no-drops-until-prod-verified`.

### Immutability

Database triggers prevent UPDATE and DELETE on the `audit_log` table, matching the pattern used for `consent_records`. `ActorUserId` is a bare cross-section Guid column with no DB-level FK constraint — a deleted/anonymized actor is handled at the application layer: `AuditEvent` (`Contracts/AuditEvent.cs`) displays "Someone" when the actor name does not resolve.

### Audit action definitions

Each writing section declares its actions as `AuditActionDefinition` fields on its `<Section>AuditActions` contributor (name, English verb and self-verb, description-tail, alert and quick-filter flags); `AuditActionRegistry` collects them at startup.

Each section lists the actions it declares in its own `Docs/<Section>.md`; this section lists none of them.

## Service Design

The section splits into a write side and a read+render side:

**`IAuditLogService` (write)** provides two `LogAsync` overloads, each taking an `AuditActionDefinition`, plus two narrow cross-section reads, `GetFilteredEntriesAsync` and `GetEntityIdsForEntityTypeActionsAsync`:

1. **Job overload** — no human actor, accepts job name (prefixed to description)
2. **Human overload** — accepts actor user ID

Each call is self-persisting via `IAuditLogRepository.AddAsync`, which opens a fresh `DbContext` via `IDbContextFactory<AuditLogDbContext>` and saves immediately (design-rules §7a). Callers do not need to flush audit, and audit does not roll back if a later business step fails.

**`IAuditViewerService` (read+render)** owns the read path. It returns resolved `AuditEvent` records — actor/subject/target-team display names are batch-resolved inside the section via `Humans.Base`'s `IEntityNameContributor` fan-out (no per-call-site `GetUserDisplayNamesAsync` / `GetTeamNamesAsync` dance, and neither method exists any more). Overloads cover the global `/AuditLog` page, per-entity history (Profile/Team/Calendar/etc.), per-user history (MemberDetail), and the agent's `get_audit_history` tool. Verbs and the description-tail flag live on each action's `AuditActionDefinition`, which backs both `AuditEvent.RenderPlainText` (agent tool output, with viewer-GUID → "You" substitution) and `RenderStructured` (the view-component HTML composition path).

`IAuditLogService` exposes no display-name lookups to controllers — name resolution lives entirely behind `IAuditViewerService`.

## Phase 1 Coverage (Current)

### Background Jobs

| Job | Actions Logged |
|-----|---------------|
| SystemTeamSyncJob | TeamMemberAdded, TeamMemberRemoved |
| SuspendNonCompliantMembersJob (via `NonCompliantMemberSuspension`) | MemberSuspended |
| ProcessAccountDeletionsJob | AccountAnonymized |

### Admin Actions

| Action | Audit action | Writer |
|--------|--------------|--------|
| Suspend member | MemberSuspended | `HumanLifecycleService`, `NonCompliantMemberSuspension` |
| Unsuspend member | MemberUnsuspended | `HumanLifecycleService` |
| Add role | RoleAssigned | `RoleAssignmentService` |
| End role | RoleEnded | `RoleAssignmentService` |

`VolunteerApproved` has no writer (reserved).

## Phase 2 Coverage (Current)

### TeamService

| Action | Audit action | Actor |
|--------|-------------|-------|
| User joins open team | TeamJoinedDirectly | User |
| User leaves team | TeamLeft | User |
| Join request approved | TeamJoinRequestApproved | Approver (Board/Coordinator) |
| Join request rejected | TeamJoinRequestRejected | Approver (Board/Coordinator) |
| Member removed by admin | TeamMemberRemoved | Admin (Board/Coordinator) |
| Member role changed | TeamMemberRoleChanged | Admin (Board/Coordinator) |

### GoogleWorkspaceSyncService

| Action | Audit action | Actor |
|--------|-------------|-------|
| Google Group linked or reactivated for a team | GoogleResourceProvisioned | GoogleWorkspaceSyncService |
| Google Group provisioned | GoogleResourceProvisioned | GoogleWorkspaceSyncService |

`GoogleResourceAccessGranted` / `GoogleResourceAccessRevoked` have no live writer: access changes live in `google_sync_log`.

### DriveActivityMonitorJob

| Action | Audit action | Actor |
|--------|-------------|-------|
| Anomalous permission change detected | AnomalousPermissionDetected | DriveActivityMonitorJob |

### Google Sync Audit — moved out

Permission-change detail (user email, role, sync source, success flag, error message) is no longer audit's business. GoogleIntegration owns `google_sync_log` and renders it through `<vc:google-sync-log>` (nobodies-collective/Humans#1083). See [GoogleIntegration.md](../../../Humans.GoogleIntegration/Docs/GoogleIntegration.md).

## User Interface

### Global Audit Log Page (`/AuditLog`)

Accessible to Board and Admin. Displays all audit log entries with filtering by action type. Features:
- Filter buttons: All, then one per definition with a `QuickFilter` label (today Anomalous Permissions, Access Granted/Revoked, Suspensions, Roles)
- `Alert` entries highlighted with warning styling
- Alert banner per `Alert` action with its row count
- Paginated (50 per page)

### Drive Activity Check (`/Monitor/CheckDriveActivity`)

POST action on `MonitorController` (see [`AuditLog.md`](../AuditLog.md#routing)). Manual trigger for the Drive Activity monitor. Redirects to `/AuditLog?filter=AnomalousPermissionDetected` after completion.

### Google sync pages (`/Google/Resource/{id}`, `/Google/Human/{id}`)

These GoogleIntegration pages render `<vc:google-sync-log>` over its `google_sync_log`. Documented in [GoogleIntegration.md](../../../Humans.GoogleIntegration/Docs/GoogleIntegration.md).

### Per-User Audit View (MemberDetail page)

Displays the 50 most recent audit entries affecting a user, queried by:
- `EntityType = 'User' AND EntityId = @userId` (direct entries)
- `RelatedEntityId = @userId` (related entries, e.g., team membership changes)

Each entry renders structurally as: `[timestamp] — [actor] [verb] [subject] in [team] — [description]`. The verb and self-verb come from the action's definition; the self-form is used when actor == subject to avoid dangling prepositions. `IAuditViewerService` resolves the page, batch-loading actor/subject/target-team display names inside the section and returning `AuditEvent` records; the shared `AuditLogViewComponent` then composes HTML around `AuditEvent.RenderStructured`, which produces the field bundle (actor/subject as clickable `<human-link>` data, verb, description tail). Unmapped actions fall back to `[actor] · [ActionName] · [subject] — [description]` so attribution is never lost.

### Agent tool — `get_audit_history`

Agent dispatcher tool that returns the calling user's audit history as plain text. Default 20 lines, hard-capped at 50 (limit clamped server-side; minimum 1). Empty result returns "No audit history for this user." Each line is the `RenderPlainText` of an `AuditEvent`, with the calling user's GUID rewritten to "You" so the agent never echoes the user's id. Used by the system prompt for personal-history questions ("who voluntold me?", "when did I get added to the Build team?", role changes, approvals).

## Authorization

The global audit log (`/AuditLog`, `AuditLogController.Index`) is visible only to Board and Admin. Per-resource and per-human sync views are `GoogleController`'s; per-user history is `<vc:audit-log>` on host pages and inherits the host page's policy. The Finance audit log (`/Finance/AuditLog`) is restricted to FinanceAdmin and Admin.

## Related Features

- [F-05: Volunteer Status](../../../Humans.Onboarding/Docs/features/volunteer-status.md) — Suspension triggers audit entries
- [F-08: Background Jobs](../../../../../docs/features/global/background-jobs.md) — Jobs are primary audit producers
- [F-09: Administration](../../../../../docs/features/global/administration.md) — Admin actions produce audit entries
- [F-06: Teams](../../../Humans.Teams/Docs/features/Teams-feature.md) — Team sync produces audit entries
- [F-13: Drive Activity Monitoring](../../../Humans.Monitor/Docs/features/drive-activity-monitoring.md) — Anomalous permission detection
