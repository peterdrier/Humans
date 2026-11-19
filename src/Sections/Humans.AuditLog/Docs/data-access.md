# AuditLog — Data Access

## AuditLog

Project: `src/Sections/Humans.AuditLog` — services under `Services/`,
repository under `Data/`. `IAuditLogService` (the append path, used
by most sections) lives in `src/Humans.Base/AuditLog/`; the section's `Contracts/`
folder keeps `IAuditViewerService`, `AuditEvent` and `ILegacyGoogleSyncAuditReader`. `AuditLog` is a **horizontal section**.
**DbContext:** `AuditLogDbContext`.
`AuditLogRepository` injects
`IDbContextFactory<AuditLogDbContext>` directly. Owns
`AuditLogEntries`.

### AuditLogService (Scoped)

Repository: `IAuditLogRepository`.

| Table | R/W |
|-------|-----|
| AuditLogEntries | R/W |

Cross-section calls via `IUserService` (migrated to the read-split
surface). Implements `IUserDataContributor`, and — until the six Google
columns are dropped — `ILegacyGoogleSyncAuditReader`, the read-only feed for
Google's one-time history migration screen.
No `IMemoryCache`.

### AuditViewerService (Scoped) — `src/Sections/Humans.AuditLog/Services/`

No repository. Read-only view assembler over the section-internal
`IAuditLogReader` plus `IEnumerable<IEntityNameContributor>` (`Humans.Base`).
No DB access, no cache. `internal sealed`; its interface and `AuditEvent` sit in the project's
`Contracts/` folder; the `/AuditLog` page read (`GetPageAsync`, `AuditEventPage`)
stays internal. It names no other
section: actor, subject and target-team display names come back from the
contributor fan-out (nobodies-collective/Humans#1059), so the section no
longer takes `Humans.Teams.Contracts` or `Humans.Google.Contracts`.

`AuditEvent` and `AuditActionDefinition` are value types / pure
formatters with no DI dependencies.

---


