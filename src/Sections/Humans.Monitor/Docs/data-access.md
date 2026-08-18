# Monitor — Data Access

## Monitor

Project: `src/Sections/Humans.Monitor` — a horizontal section. Owns no DB
tables and has no `Data/` folder of its own. Owns
`DriveActivityMonitorService` (+ `IDriveActivityMonitorService` in
`Contracts/`, + `DriveActivityMonitorJob` in `Jobs/`); Google Integration
owns the Drive/Directory API clients Monitor calls into
(`Services/Workspace/`).

### DriveActivityMonitorService (Scoped)

No repository.

| Table | R/W |
|-------|-----|
| SystemSettings | R/W (key `DriveActivityMonitor:LastRunAt`, **via `ISystemSettingsService`** — owned by the SystemSettings section) |
| GoogleResources | R (via `ITeamResourceService` — the GoogleIntegration section) |
| Users / IdentityUserLogins | R (via `IUserServiceRead.GetAllUserInfosAsync` / `UserInfo.ExternalLogins`) |

Monitors the Drive Activity API for non-service-account permission changes
on managed Drive resources and logs anomaly audit entries. Google
`people/{id}` fallback resolution goes through the Users read-model:
the service builds a per-run Google provider-key -> `UserInfo` index from
`IUserServiceRead.GetAllUserInfosAsync` and uses `UserInfo.Email`. The
last-run marker (`SystemSettingKeys.DriveActivityMonitorLastRunAt`) is read
and written through `ISystemSettingsService` — the SystemSettings section's
repository, not its own. Audit-log writes go through `IAuditLogService`.

Cross-section calls via `IGoogleDriveActivityClient` (GoogleIntegration —
`Services/Workspace/`),
`ITeamResourceService`, `ISystemSettingsService`, `IUserServiceRead`,
`IAuditLogService`. No cache.

**Cross-section table read/write (design-rule note):** `SystemSettings`
is read/written through the owning SystemSettings section's
`ISystemSettingsService` — this is the §15-compliant cross-section call
(service interface, not a foreign repository), so it is **not** a violation.

---


