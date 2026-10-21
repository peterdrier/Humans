# Teams — Data Access

## Teams

Project: `src/Sections/Humans.Teams`, with a `src/Sections/Humans.Teams.Contracts`
leaf for the cross-section surface. Structure: `Domain/` (the entities,
internal sealed), `Data/` (`ITeamRepository`/`TeamRepository`, `TeamsDbContext`
+ factory, EF configurations, migrations), `Services/`, `Controllers/`,
`Models/`, `Views/`, `ViewComponents/`, `Authorization/`, `Contracts/` (public
`HumansTeamControllerBase`, derives from `HumansControllerBase` in
`Humans.Base`, which the leaf must not carry because Shifts' admin controller
needs it and a leaf may not reference Base's UI layer), and the `TeamsResource`
resx set at the project root. **DbContext:**
`TeamsDbContext` (`TeamRepository` injects
`IDbContextFactory<TeamsDbContext>`). Owns `Teams`,
`TeamMembers`, `TeamJoinRequests`, `TeamJoinRequestStateHistories`,
`TeamRoleAssignments`, `TeamRoleDefinitions`, `TeamEarlyEntryGrants`. On team
mutations it also emits `GoogleSyncOutboxEvents` — those go
through `IGoogleSyncOutboxService` (the Google Integration section's write
surface) inside a `TransactionScope`, not through `TeamRepository`. The
table is therefore owned wholly by Google Integration.

The `Humans.Teams.Contracts` leaf carries two public faces:
`ITeamService` (the cross-section read surface, `TeamInfo` /
`TeamSearchHit` / `UserTeamMembershipInfo` projections only)
and `ITeamWrite` (flat projections — only the writes some other section
calls), both `IApplicationService`; the internal
`ITeamManagement : ITeamService, ITeamWrite` and
`ITeamManagementService : ITeamManagement` in `Services/` carry the rest
(create/update/role-assignment/EE-grant mutation surface, the coordinator-role
reconciliation the hourly job runs). The leaf also
carries `ITeamSeeding` (implemented explicitly by `TeamService` /
`CachingTeamService`) for the dev fixture seeders in `Humans.Development`
and `Humans.Budget`, `ISystemTeamSync` (the reconciler's contract, registered
in `Section.cs`) and `IActiveTeamsCacheInvalidator` (registered in `Section.cs`;
Users evicts through it).

The section exposes no entity-returning reads — external consumers call
`GetTeamAsync` / `GetTeamBySlugAsync` (`TeamInfo`), `GetUserTeamMembershipsAsync`
(`UserTeamMembershipInfo`), and `GetTeamsWithParentsAsync` (`TeamInfo`); no
entity type crosses the section boundary. The `TeamAuthorizationHandler`
registration lives in the section; its policies stay in Shell.

Teams is an `IEarlyEntryProvider` (role-gated team early entry,
cantina-style). `ITeamRepository` has an EE-grant surface
(`GetEarlyEntryGrantsForEnabledTeamsAsync`,
`GetEarlyEntryGrantsForTeamAsync`, `GetEarlyEntryGrantsForUserAsync`,
`FindEarlyEntryGrantForMutationAsync`, `Add/Update/RemoveEarlyEntryGrantAsync`,
`ReassignEarlyEntryGrantsAsync`, `RemoveEarlyEntryGrantsForUserAsync`),
backed by the `team_early_entry_grants` table that `TeamRepository` owns.
`TeamService` contributes a GDPR slice
under the export key declared as `TeamService.TeamEarlyEntry`; the
`IUserMerge` path reassigns grants across the merge.

Team search is cache-only: `CachingTeamService.SearchAsync` filters the
cached `TeamInfo` snapshot (hidden teams excluded unless requested); the
inner `TeamService.SearchAsync` **throws `NotSupportedException`** — there
is no DB-backed search path.

The inner `ITeamService`
registration is wrapped by
`Humans.Teams.Services.CachingTeamService` (Singleton
decorator inheriting `TrackedCache<Guid, TeamInfo>`); it exposes the
cross-section read surface as `ITeamService`.

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
| `shift-auth:{userId}` (`IShiftAuthorizationInvalidator`) | yes |

Cross-section calls via `IAuditLogService`, `INotificationEmitter`,
`IShiftService`, `IAdminAuthorizationService`,
`IShiftAuthorizationInvalidator`; lazy-resolved via `IServiceProvider` for
cycle-breaking: `IGoogleSyncOutboxService` (transactional outbox appends),
`IGoogleSyncWrite`, `ITeamResourceService`, `IRoleAssignmentService`,
`IEmailService`, `IUserService`, `ISystemTeamSync`. Implements `ITeamManagementService`
(internal, `: ITeamManagement`), `ITeamSeeding`,
`IGoogleGroupMembershipSource`, `IUserDataContributor`, `IUserMerge`,
`IEarlyEntryProvider` (role-gated team early entry).

### TeamDriveAccessSource (Scoped)

Implements `IGoogleDriveAccessSource` over the existing `ITeamService` and `ITeamResourceService` bulk projections. Owns membership union, maximum permission levels and active child-team rollup for linked Drive resources. Returns user-ID claims and team-link display metadata; inactive teams retain empty claims so reconciliation can revoke grants before retiring linked resources. No repository or cache.

**Outbox write goes through the owning service:**
`GoogleSyncOutboxEvents` is owned by the Google Integration section.
`TeamService` does not reach into the table via `TeamRepository`; it calls
`IGoogleSyncOutboxService.AddAsync` / `AddRangeAsync` inside a
`TransactionScope` so the team mutation and the outbox append commit
atomically — a cross-section call through the service interface.

### TeamMessageOptionsProvider (Scoped, internal)

No repository, no cache. `ITeamMessageOptionsProvider`: the teams a viewer may send a
facilitated message as — active teams the viewer coordinates whose linked Google Group is
synced. Reads `ITeamService.GetTeamsAsync` and GoogleIntegration's
`ITeamResourceService.GetResourcesByTeamIdsAsync`. Callers: `TeamMessageController` and
`TeamMessageButtonsViewComponent`.

### CachingTeamService (Singleton, `Humans.Teams.Services`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, TeamInfo>` (`Team.TeamInfo`, in-process, no `IMemoryCache`) | Per-Entity (warmed on startup) | yes | yes | yes (via `IActiveTeamsCacheInvalidator`, called from `IUserMerge` flows and direct mutation paths) |

Implements `ITeamManagementService`, `ITeamSeeding`, `IUserMerge`. The
`TeamInfo` `TrackedCache` is the canonical source — no `ActiveTeams`
`IMemoryCache` entry. `SearchAsync` is served from the cached `TeamInfo`
snapshot, never the DB. Surfaced on `/Debug/CacheStats`.

### TeamPageService / TeamPageSummaryMapper / TeamDirectoryBuilder

Read-only assemblers — no repository, no cache. `TeamPageService` fans out
over `ITeamManagementService`, `ITeamResourceService`,
`IShiftService`, `ISettingsService`, `IUserService` and Auth’s
`IRoleAssignmentService`. It owns detail-page visibility, viewer affordances and
member/child projections over the cached `TeamInfo` snapshot, with live
pending-request reads through `ITeamManagementService`. The My Teams projection
uses the cached team and user-membership reads for parent names and manageable
pending counts, keeping Auth role checks out of the decorator;
the roster expands the cached role definitions of active non-system teams and resolves assigned-user names through Users.
The mapper is pure. The directory page service passes the same cached snapshot and Auth role checks
to the directory builder, which hides private parent metadata; directory reads do not reload the inner dataset.

### TeamsEmails (Scoped, internal)

No repository. Pure builder — reads `TeamsResource` (via
`IStringLocalizer<TeamsResource>`) and `EmailSettings`, writes nothing.
Returns `EmailMessage` values for `TeamService` and `SystemTeamSyncJob` to
pass to `IEmailService.SendAsync`. No DB access, no cache.

### TeamsEmailPreviews (Scoped)

No repository. Read-only gallery contributor (`IEmailPreviewContributor`,
registered in `Section.Register`) — builds one sample per template via
`TeamsEmails` for `/Email/EmailPreview`. No DB access, no cache.

---


