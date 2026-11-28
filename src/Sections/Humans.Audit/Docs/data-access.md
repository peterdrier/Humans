# Audit — Data Access

## Audit

Project: `src/Sections/Humans.Audit` — services under `Services/`,
repository under `Data/`. `IAuditLogService` (the append path, used
by most sections) lives in `src/Humans.Base/Audit/`; the section's `Contracts/`
folder keeps `IAuditViewerService`, `AuditEntry` and `ILegacyGoogleSyncAuditReader`. `Audit` is a **horizontal section**.
**DbContext:** `AuditLogDbContext`.
`AuditLogRepository` injects
`IDbContextFactory<AuditLogDbContext>` directly. Owns
`AuditLogEntries` (permanent) and `TemporaryAuditLogEntries`.

### AuditLogService (Scoped)

Repository: `IAuditLogRepository`.

| Table | R/W |
|-------|-----|
| AuditLogEntries | R/W (no update or delete) |
| TemporaryAuditLogEntries | R/W/D (delete by age and by erasure only) |

Cross-section calls via `IUserService` (migrated to the read-split
surface). Implements `IUserDataContributor`, `IRetentionPeriodContributor` (Base; the temporary side), and — until the six Google
columns are dropped — `ILegacyGoogleSyncAuditReader`, the read-only feed for
Google's one-time history migration screen.
No `IMemoryCache`.

### AuditViewerService (Scoped) — `src/Sections/Humans.Audit/Services/`

No repository. Read-only view assembler over the section-internal
`IAuditLogReader` plus `IEnumerable<IEntityNameContributor>` (`Humans.Base`).
No DB access, no cache. `internal sealed`; its interface and `AuditEntry` sit in the project's
`Contracts/` folder; the `/Audit` page read (`GetPageAsync`, `AuditEntryPage`)
stays internal. It names no other
section: actor, subject and target-team display names come back from the
contributor fan-out (nobodies-collective/Humans#1059), so the section no
longer takes `Humans.Teams.Contracts` or `Humans.Google.Contracts`.

`AuditEntry` and `AuditActionDefinition` are value types / pure
formatters with no DI dependencies.

---


