<!-- freshness:triggers
  src/Sections/Humans.Monitor/**
  tests/Humans.Monitor.Tests/**
-->

# Monitor — target shape

Monitor watches managed Drive folders for permission changes made outside Humans. The hourly
job and the Board/Admin manual action both call `DriveActivityMonitorService`; detected changes
are written to AuditLog.

## Structure

- `Contracts/IDriveActivityMonitorService.cs` — the one-method scan contract.
- `Services/DriveActivityMonitorService.cs` — queries activity, filters the service account,
  advances the Settings marker, and writes anomaly audit entries.
- `Jobs/DriveActivityMonitorJob.cs` and `SectionJobs.cs` — the hourly trigger.
- `Controllers/MonitorController.cs` — the manual trigger and operator-safe error handling.
- `Section.cs` — DI registration.

## Invariants

- Monitor owns no tables, repository, views, or resource set.
- The marker advances only after a complete configured scan.
- A run that cannot query any resource throws; the manual action catches that failure and
  displays an error banner, while the job records a failed run.
- The controller catch path is pinned in `Humans.Monitor.Tests`, whose ASP.NET Core framework
  reference exists specifically so CI can construct the controller.

GoogleIntegration owns and renders the separate sync-audit pages. Keeping those pages beside
`GoogleSyncLogService` removes Monitor's former implementation reference and silent Razor tag
helper dependency.
