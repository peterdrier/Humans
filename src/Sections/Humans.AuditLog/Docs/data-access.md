# AuditLog — Data Access

## AuditLog

Project: `src/Sections/Humans.AuditLog` — services under `Services/`,
repository under `Data/`, plus a `src/Sections/Humans.AuditLog.Contracts`
leaf carrying `IAuditLogService` and `AuditLogEntrySnapshot` (~130 consumer
files, mostly in Base, hence a leaf project rather than a `Contracts/`
folder). `AuditLog` is a **horizontal section**.
**DbContext:** `AuditLogDbContext`.
`AuditLogRepository` injects
`IDbContextFactory<AuditLogDbContext>` directly. Owns
`AuditLogEntries`.

### AuditLogService (Scoped)

Repository: `IAuditLogRepository`.

| Table | R/W |
|-------|-----|
| AuditLogEntries | R/W |

Cross-section calls via `IUserServiceRead` (migrated to the read-split
surface). Implements `IUserDataContributor`.
No `IMemoryCache`.

### AuditViewerService (Scoped) — `src/Sections/Humans.AuditLog/Services/`

No repository. Read-only view assembler over `IAuditLogService`,
`IUserServiceRead`, `ITeamServiceRead`, `ITeamResourceService`. No DB
access, no cache. `internal sealed`; its interface, `AuditEvent` and
`AuditEventPage` sit in the project's `Contracts/` folder. The section
takes `Humans.Teams.Contracts`, `Humans.GoogleIntegration.Contracts` and
`Humans.Users.Contracts`. Team names are stitched by filtering the cached
`ITeamServiceRead.GetTeamsAsync()` `TeamInfo` dictionary client-side (only
`Name`/`Slug` are consumed; parent data is not used).

`AuditEvent` and `AuditEventTextualizer` are value types / pure
formatters with no DI dependencies.

---


