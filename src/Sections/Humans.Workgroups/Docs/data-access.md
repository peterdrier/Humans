# Workgroups — Data Access

## Workgroups

Folder: `src/Sections/Humans.Workgroups/Services/` (namespace `Humans.Workgroups.Services`).
**DbContext:** `WorkgroupsDbContext`, own `__EFMigrationsHistory_Workgroups` table,
migrations under `Data/Migrations/`. `WorkgroupRepository`
(`src/Sections/Humans.Workgroups/Data/WorkgroupRepository.cs`, `internal`) injects
`IDbContextFactory<WorkgroupsDbContext>` directly, one context per call, `AsNoTracking`
on reads. Owns all six tables: `workgroups`, `workgroup_members`, `workgroup_meetings`,
`workgroup_log_entries`, `workgroup_documents`, `workgroup_document_comments`.

Every cross-section reference (`AppliedByUserId`, `UserId`, `CreatedByUserId`,
`AuthorUserId`, `RespondedByUserId`, `HiddenByUserId`, `DispositionByUserId`,
`SurveyId`) is a bare Guid column — no FK, no navigation
(`memory/architecture/no-cross-section-ef-joins.md`). Intra-section FKs (Workgroup →
Members/Meetings/LogEntries/Documents, cascade; Document → Comments, cascade; LogEntry →
Document, `SetNull`) use EF navigations as usual.

The inner `IWorkgroupService` is wrapped by `Humans.Workgroups.Services.CachingWorkgroupService`
(Singleton decorator). It owns one `TrackedCache<byte, IReadOnlyList<WorkgroupInfo>>`
(`Workgroups.Register`) — a single entry for the whole register, since the dataset is a
few dozen groups. Every write delegates to the inner service, then clears the cache
whole (no per-group invalidation tracking).

### WorkgroupService (Scoped, keyed `"workgroups-inner"` — inner of CachingWorkgroupService)

Repository: `IWorkgroupRepository`.

| Table | R/W |
|-------|-----|
| workgroups | R/W |
| workgroup_members | R/W |
| workgroup_meetings | R/W |
| workgroup_log_entries | R/W |
| workgroup_documents | R/W |
| workgroup_document_comments | R/W |

Cross-section calls: `IUserServiceRead`, `IUserEmailService`, `IRoleAssignmentService`,
`ITeamServiceRead`, `ISettingsService`, `IGoogleSyncService`, `INotificationService`,
`IEmailService`, `IEmailMessageFactory`, `IAuditLogService`, `IClock` (NodaTime). The
inner service has no `IMemoryCache`.

### CachingWorkgroupService (Singleton, `Humans.Workgroups.Services`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<byte, IReadOnlyList<WorkgroupInfo>>` (`Workgroups.Register`) | Single entry | yes | yes | yes (full `Clear()` after every delegated write) |

Implements `IWorkgroupService`, `IUserDataContributor` and `IUserMerge` (erasure and the
merge fold change cached rows, so both bind to the decorator, not the inner). Exposes
`ICacheStats RegisterCacheStats` on `/Debug/CacheStats`. No warmup —
`warmOnStartup: false`; the register populates lazily on the first read after a miss.
Single-group reads (`GetBySlugAsync`, `GetByIdAsync`) come off the cached register rather
than a second query, since the graph is already whole.

### Inbound fan-out implementations (still `WorkgroupService`-backed, no tables of their own)

- `WorkgroupCalendarContributor` (`Services/Contributors/`) implements
  `ICalendarFeedContributor` by reading the cached register through `IWorkgroupService` —
  no direct repository access.
- `WorkgroupDriveAccessSource` (`Services/Contributors/`) implements
  `IGoogleDriveAccessSource` the same way, plus `IUserServiceRead`/`ITeamServiceRead` for
  the root folder's reader list.

Neither owns a table or touches `WorkgroupsDbContext` directly — both go through
`IWorkgroupService`, consistent with "repository is the only DbContext caller."

---
