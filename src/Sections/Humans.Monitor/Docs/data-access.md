<!-- freshness:triggers
  src/Sections/Humans.Monitor/Services/DriveActivityMonitorService.cs
  src/Sections/Humans.Monitor/Controllers/MonitorController.cs
-->

# Monitor — Data Access

## Monitor

Project: `src/Sections/Humans.Monitor` — a leaf-consumer section. Owns no DB
tables and has no `Data/` folder of its own. Owns
`DriveActivityMonitorService` (+ `IDriveActivityMonitorService` in
`Contracts/`, + `DriveActivityMonitorJob` in `Jobs/`); Google Integration
owns the Drive/Directory API clients Monitor calls into
(`Services/Workspace/`).

### DriveActivityMonitorService (Scoped)

No repository.

| Table | R/W |
|-------|-----|
| SystemSettings | R/W (key `DriveActivityMonitor:LastRunAt`, **via `ISettingsService`** — owned by the Settings section) |
| GoogleResources | R (via `ITeamResourceService` — the GoogleIntegration section) |
| Users / IdentityUserLogins | R (via `IUserServiceRead.GetAllUserInfosAsync` / `UserInfo.ExternalLogins`) |

Monitors the Drive Activity API for non-service-account permission changes
on managed Drive resources and logs anomaly audit entries. Google
`people/{id}` fallback resolution goes through the Users read-model:
the service builds a per-run Google provider-key -> `UserInfo` index from
`IUserServiceRead.GetAllUserInfosAsync` and uses `UserInfo.Email`. The
last-run marker (`SettingKeys.DriveActivityMonitorLastRunAt`) is read
and written through `ISettingsService` — the Settings section's
repository, not its own. Audit-log writes go through `IAuditLogService`.

Cross-section calls via `IGoogleDriveActivityClient` (GoogleIntegration —
`Services/Workspace/`),
`ITeamResourceService`, `ISettingsService`, `IUserServiceRead`,
`IAuditLogService`. No cache.

**Cross-section table read/write (design-rule note):** `SystemSettings`
is read/written through the owning Settings section's
`ISettingsService` — this is the §9-compliant cross-section call
(service interface, not a foreign repository), so it is **not** a violation.

---
