# Teams — Data Access

## Teams

Project: `src/Sections/Humans.Teams`, with a `src/Sections/Humans.Teams.Contracts`
leaf for the cross-section surface. Structure: `Domain/` (7 entities,
internal sealed), `Data/` (`ITeamRepository`/`TeamRepository`, `TeamsDbContext`
+ factory, EF configurations, migrations), `Services/`, `Controllers/`,
`Models/`, `Views/`, `Resources/`, `Authorization/`, `Contracts/` (public
`HumansTeamControllerBase`, derives from `HumansControllerBase` in
`Humans.UI`, which no leaf may reference). **DbContext:**
`TeamsDbContext` (`TeamRepository` injects
`IDbContextFactory<TeamsDbContext>`). Owns `Teams`,
`TeamMembers`, `TeamJoinRequests`, `TeamJoinRequestStateHistories`,
`TeamRoleAssignments`, `TeamRoleDefinitions`, `TeamEarlyEntryGrants`. On team
mutations it also emits `GoogleSyncOutboxEvents` — those go
through `IGoogleSyncOutboxService` (the Google Integration section's write
surface) inside a `TransactionScope`, not through `TeamRepository`. The
table is therefore owned wholly by Google Integration.

The `Humans.Teams.Contracts` leaf carries three interface levels:
`ITeamServiceRead` (5 members, `[SurfaceBudget(5)]`)
and `ITeamService` (~20 members, flat projections — extends
`ITeamServiceRead` + `IApplicationService`) on the leaf; the internal
`ITeamManagementService : ITeamService` in `Services/` carries the rest
(create/update/role-assignment/EE-grant mutation surface). The leaf also
carries `ITeamSeeding` (implemented explicitly by `TeamService` /
`CachingTeamService`) for the dev fixture seeders in `Humans.Development`
and `Humans.Budget`.

The section exposes no entity-returning reads — external consumers call
`GetTeamAsync` / `GetTeamBySlugAsync` (`TeamInfo`), `GetUserTeamMembershipsAsync`
(`UserTeamMembershipInfo`), and `GetTeamsWithParentsAsync` (`TeamInfo`); no
entity type crosses the section boundary. `IActiveTeamsCacheInvalidator` and
`ISystemTeamSync` live in Shell's `InfrastructureServiceCollectionExtensions`
(neither is Teams-owned); the `TeamAuthorizationHandler` registration lives
in the section while its policies stay in Shell.

Teams is an `IEarlyEntryProvider` (role-gated team early entry,
cantina-style). `ITeamRepository` has an EE-grant surface
(`GetEarlyEntryGrantsForEnabledTeamsAsync`,
`GetEarlyEntryGrantsForTeamAsync`, `GetEarlyEntryGrantsForUserAsync`,
`FindEarlyEntryGrantForMutationAsync`, `Add/Update/RemoveEarlyEntryGrantAsync`,
`ReassignEarlyEntryGrantsAsync`, `RemoveEarlyEntryGrantsForUserAsync`),
backed by the `team_early_entry_grants` table that `TeamRepository` owns.
`TeamService` injects `IEarlyEntryInvalidator` and contributes a
`GdprExportSections.TeamEarlyEntry` GDPR slice; the `IUserMerge` path
reassigns grants across the merge.

Team search is cache-only: `CachingTeamService.SearchAsync` filters the
cached `TeamInfo` snapshot (hidden teams excluded unless requested); the
inner `TeamService.SearchAsync` **throws `NotSupportedException`** — there
is no DB-backed search path.

The inner `ITeamService`
registration is wrapped by
`Humans.Teams.Services.CachingTeamService` (Singleton
decorator inheriting `TrackedCache<Guid, TeamInfo>`, still in the section's
own `Services/` folder post-move); it exposes the
budgeted cross-section read surface as `ITeamServiceRead`.

### TeamService (Scoped — wrapped by CachingTeamService Singleton decorator)

Repository: `ITeamRepository`.

| Table | R/W |
|-------|-----|
| Teams | R/W |
| TeamMembers | R/W |
| TeamJoinRequests | R/W |
| TeamJoinRequestStateHistories | R/W |
| TeamRoleAssignments | R/W |
| TeamRoleDefinitions | R |
| TeamEarlyEntryGrants | R/W |
| GoogleSyncOutboxEvents | W via `IGoogleSyncOutboxService` (not the Teams repo) |

| Cache (via invalidators) | Invalidate |
|-------------------------|------------|
| `NotificationMeters` (`INotificationMeterCacheInvalidator`) | yes |
| `shift-auth:{userId}` (`IShiftAuthorizationInvalidator`) | yes |
| `EarlyEntry.UserEarlyEntry` TrackedCache (`IEarlyEntryInvalidator`) | yes (per-user on grant writes / merge; `InvalidateAll` on team EE-flag flip) |

Cross-section calls via `IAuditLogService`, `INotificationEmitter`,
`IShiftManagementService`, `IAdminAuthorizationService`,
`IEarlyEntryInvalidator`, `IGoogleSyncOutboxService` (lazy-resolved via
`IServiceProvider`, for transactional outbox appends), plus
`IServiceProvider` for cycle-breaking. Implements `ITeamManagementService`
(internal, `: ITeamService`), `ITeamSeeding`,
`IGoogleGroupMembershipSource`, `IUserDataContributor`, `IUserMerge`,
`IEarlyEntryProvider` (role-gated team early entry).

**Outbox write goes through the owning service:**
`GoogleSyncOutboxEvents` is owned by the Google Integration section.
`TeamService` does not reach into the table via `TeamRepository`; it calls
`IGoogleSyncOutboxService.AddAsync` / `AddRangeAsync` inside a
`TransactionScope` so the team mutation and the outbox append commit
atomically — a §15-compliant cross-section call (service interface).

### CachingTeamService (Singleton, `Humans.Teams.Services`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, TeamInfo>` (`Team.TeamInfo`, in-process, no `IMemoryCache`) | Per-Entity (warmed on startup) | yes | yes | yes (via `IActiveTeamsCacheInvalidator`, called from `IUserMerge` flows and direct mutation paths) |

Implements `ITeamManagementService`, `ITeamSeeding`, `IUserMerge`. The
`TeamInfo` `TrackedCache` is the canonical source — no `ActiveTeams`
`IMemoryCache` entry. `SearchAsync` is served from the cached `TeamInfo`
snapshot, never the DB. Surfaced on `/Debug/CacheStats`.

### TeamPageService / TeamPageSummaryMapper / TeamDirectoryBuilder

Read-only assemblers — no repository, no cache. Fan out over
`ITeamService`, `IUserService`, `ITeamResourceService`,
`IShiftManagementService`.

---


