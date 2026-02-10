# F-13: Drive Activity Monitoring

## Business Context

Nobodies Collective manages Google Shared Drive folders and Groups through the system. Permission changes made outside the system (e.g., someone manually adding/removing permissions via Google Drive UI) can create security risks and data inconsistencies. The Board needs visibility into these anomalous changes to maintain control over who has access to team resources.

## User Stories

### US-13.1: Automatic Anomaly Detection
**As a** system administrator
**I want** the system to automatically check for permission changes not made by the system
**So that** I am alerted to unauthorized access modifications

**Acceptance Criteria:**
- Background job runs hourly via Hangfire
- Checks Google Drive Activity API for permission changes on all active Drive folder resources
- Identifies changes NOT initiated by the system's service account
- Logs each anomalous change to the audit log with `AnomalousPermissionDetected` action
- Includes actor identity, affected resource, and specific permission changes in the log entry

### US-13.2: Manual Activity Check
**As a** Board member or Admin
**I want to** manually trigger a Drive activity check
**So that** I can verify the current state on demand

**Acceptance Criteria:**
- "Check Drive Activity Now" button on the Audit Log page
- Shows result count after completion
- Redirects to filtered audit log view showing anomalous permission entries

### US-13.3: Audit Log View with Anomaly Alerts
**As a** Board member or Admin
**I want to** view and filter audit log entries
**So that** I can review system activity and investigate anomalies

**Acceptance Criteria:**
- Global audit log page at `/Admin/AuditLog`
- Filter by action type (All, Anomalous Permissions, Access Granted/Revoked, Suspensions, Roles)
- Anomalous entries highlighted with warning styling
- Alert banner showing total anomaly count
- Paginated (50 per page)
- Accessible from admin dashboard quick actions

## Architecture

### Clean Architecture Layers

**Domain:**
- `AuditAction.AnomalousPermissionDetected` enum value (stored as string, no migration needed)

**Application:**
- `IDriveActivityMonitorService` interface with `CheckForAnomalousActivityAsync` method

**Infrastructure:**
- `DriveActivityMonitorService` - real implementation using Google Drive Activity API v2
- `StubDriveActivityMonitorService` - stub for development without Google credentials
- `DriveActivityMonitorJob` - Hangfire background job wrapper

**Web:**
- `AdminController.AuditLog` action - paginated audit log with filtering
- `AdminController.CheckDriveActivity` action - manual trigger
- `AuditLog.cshtml` view

### Service Registration

Follows the same conditional pattern as other Google services:
- With Google credentials configured: `DriveActivityMonitorService`
- Without credentials: `StubDriveActivityMonitorService`

### NuGet Package

`Google.Apis.DriveActivity.v2` added to `Directory.Packages.props` and `Humans.Infrastructure.csproj`.

## Drive Activity API Integration

### API Scope
`https://www.googleapis.com/auth/drive.activity.readonly` (read-only)

### Query Pattern
```
POST https://driveactivity.googleapis.com/v2/activity:query
{
  "itemName": "items/{driveFileId}",
  "filter": "time >= \"2025-01-01T00:00:00Z\"",
  "pageSize": 100
}
```

### Detection Logic

For each active Drive folder resource:
1. Query Drive Activity API for activities in the last 24 hours
2. Filter to permission change activities (`PrimaryActionDetail.PermissionChange != null`)
3. Check if any actor is the system's service account (by `KnownUser.PersonName` email match)
4. If NOT initiated by the service account, log as anomalous

### Actor Identification

The Drive Activity API identifies actors as:
- `User.KnownUser.PersonName` - user email address
- `Administrator` - Google Workspace admin
- `System` - Google system action

### Service Account Email Extraction

Parsed from the service account JSON key file's `client_email` field, matching the pattern used by `TeamResourceService`.

## Data Model

Uses the existing `AuditLogEntry` entity. No new tables or migrations required.

### Audit Log Entry Fields for Anomalies

| Field | Value |
|-------|-------|
| Action | `AnomalousPermissionDetected` |
| EntityType | `GoogleResource` |
| EntityId | GoogleResource.Id (from DB) |
| Description | Human-readable: "Anomalous permission change on '{name}' by {actor}: {details}" |
| ActorName | `DriveActivityMonitorJob` |
| ActorUserId | `null` (system job) |

## Background Job

### Schedule
```
Cron: Hourly
Job ID: drive-activity-monitor
```

### Lookback Window
24 hours from current time. This provides overlap between hourly runs to avoid missing any activities due to API propagation delays.

## Admin UI

### Route: `/Admin/AuditLog`

| Route | Method | Action |
|-------|--------|--------|
| `AuditLog` | GET | View paginated audit log with optional `action` filter |
| `AuditLog/CheckDriveActivity` | POST | Trigger manual Drive activity check |

### Filter Options
- All (no filter)
- Anomalous Permissions (`AnomalousPermissionDetected`)
- Access Granted (`GoogleResourceAccessGranted`)
- Access Revoked (`GoogleResourceAccessRevoked`)
- Suspensions (`MemberSuspended`)
- Roles (`RoleAssigned`)

### Visual Indicators
- Anomaly count badge on filter button
- Warning alert banner when anomalies exist
- Row-level warning highlight for anomalous entries

## Authorization

Inherits from `AdminController`'s `[Authorize(Roles = "Board,Admin")]`.

## Limitations

- **Drive folders only:** Google Groups do not support the Drive Activity API. Group membership changes are detected by the existing drift detection in `PreviewSyncAllAsync`.
- **24-hour lookback:** Activities older than 24 hours from the last check may be missed if the job fails to run. The hourly schedule with 24-hour lookback provides significant overlap.
- **Actor identification:** The Drive Activity API may not always provide the actor's email (e.g., for external users). In such cases, the actor is logged as "unknown".

## Related Features

- [F-07: Google Integration](07-google-integration.md) - Manages the resources being monitored
- [F-08: Background Jobs](08-background-jobs.md) - Job scheduling
- [F-12: Audit Log](12-audit-log.md) - Audit log infrastructure
