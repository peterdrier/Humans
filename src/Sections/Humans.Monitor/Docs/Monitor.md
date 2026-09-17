<!-- freshness:triggers
  src/Sections/Humans.Monitor/**
  src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs
-->
<!-- freshness:flag-on-change
  Monitor's reference set is its whole reason to exist — review the dependency list when any ProjectReference is added.
-->

# Monitor — Section Invariants

Monitor is the detective control over permission changes that GoogleIntegration's service
account did not make. It owns the scan and its triggers; GoogleIntegration owns its sync-log
pages and data.

## Concepts

- **Anomalous permission change** — a grant or revocation on a managed Drive folder made by
  someone other than the service account. Each is written through `IAuditLogService` as
  `AuditAction.AnomalousPermissionDetected`.
- **Time-window dedup** — a successful configured scan advances
  `DriveActivityMonitor:LastRunAt` through `ISettingsService`; an incomplete or stubbed scan
  does not.

## Actors / Roles

| Route | Policy |
|---|---|
| `POST /Monitor/CheckDriveActivity` | `BoardOrAdmin` |

## Invariants

- Monitor owns no tables, repository, migrations, resource set, or views.
- `DriveActivityMonitorService` calls other sections only through
  `IGoogleDriveActivityClient`, `ITeamResourceService`, `ISettingsService`,
  `IUserServiceRead`, and `IAuditLogService`.
- A service-account change, matched by email or `people/{client_id}`, is never anomalous.
- The last-run marker advances only when every resource was queried and the connector is
  configured. Anomalies are still audited on an incomplete run.
- When every resource query fails, the service throws so Hangfire records a failed run.
- The manual controller action catches that exception, logs it, shows an error banner, and
  redirects to the filtered Audit Log page.
- A Volunteer reaching the manual route is redirected to `AccessDeniedPath`.

## Triggers

- `DriveActivityMonitorJob` runs hourly.
- `POST /Monitor/CheckDriveActivity` runs the same scan from the Audit Log toolbar.

## Cross-section dependencies

| Section | Through |
|---|---|
| GoogleIntegration | `IGoogleDriveActivityClient`, `ITeamResourceService` |
| AuditLog | `IAuditLogService` |
| Settings | `ISettingsService` |
| Users | `IUserServiceRead` |

Google sync history is shown at `/Google/Resource/{id}` and `/Google/Human/{id}` by
GoogleIntegration. Monitor has no implementation-project reference to that section.
