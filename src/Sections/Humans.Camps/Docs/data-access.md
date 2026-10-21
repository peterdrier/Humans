# Camps — Data Access

## Camps

Folder: `src/Sections/Humans.Camps/Services/`, repository under `Data/`.
**DbContext:** `CampsDbContext`. `CampRepository` injects
`IDbContextFactory<CampsDbContext>` directly. Owns `Camps`, `CampSeasons`,
`CampHistoricalNames`, `CampImages`, `CampSettings`, `CampMembers`,
`CampRoleDefinitions`, `CampRoleAssignments`.

Lead authority is `CampRoleAssignment` (against the `SpecialRole=Lead`
definition) — there is no separate `camp_leads` table. `SeedSystemRolesAsync`
seeds role definitions for fresh environments.

Camp-role persistence is the `.Roles.cs` partial of `ICampRepository`;
`CampRoleService` injects `ICampRepository` directly.
The Camps section is a single repository owning all of its tables.
`IEarlyEntryProvider` is implemented by `CampService`; the registered
`CachingCampService` delegates to that inner projection. Active-event settings
and grant assembly belong to the inner service, not the cache shell. `CampRoleService` does not inject the full
`ICampManagement`; it takes the narrow intra-section `ICampRoleCampAccess`
(implemented only by `CachingCampService`) for settings, camp-member status and the compliance season list.
`ICampRoleService` itself resolves to `CachingCampService`, which passes each
call to the inner `CampRoleService` and drops the cached `CampInfo` after a
role write that changed a row.

Camp search is cache-only: `CachingCampService.SearchAsync` filters the
cached `CampInfo` snapshot (relevance-ranked, public-status gated); the
inner `CampService.SearchAsync` **throws `NotSupportedException`**
(reaching it indicates a DI registration mistake) — there is no DB-backed
search path.

### CampService (Scoped — wrapped by CachingCampService Singleton decorator)

Repository: `ICampRepository`.

| Table | R/W |
|-------|-----|
| Camps | R/W |
| CampSeasons | R/W |
| CampHistoricalNames | R/W |
| CampImages | R/W |
| CampSettings | R/W |
| CampMembers | R/W |

| Cache (via invalidators) | Invalidate |
|-------------------------|------------|
| `EarlyEntry.UserEarlyEntry` TrackedCache (`IEarlyEntryInvalidator`) | yes (camp-lead grant changes) |

The `Camp.CampInfo` projection is evicted by the decorator after the inner
write returns; the inner service never signals it.

Cross-section calls via `IUserService`, `ITicketService` (has the member entered the event),
`IAuditLogService`, `ISystemTeamSync`, `IFileStorage`, `INotificationEmitter`,
`IEarlyEntryInvalidator`, `ISettingsService` (active event settings), plus
`Lazy<ICampRoleService>` and `Lazy<ICityPlanningWrite>` (camp deletion removes
the camp's polygon/history rows) to break DI cycles. Implements `ICampLeadDirectory`, `IEarlyEntryProvider` (via `ICampManagement`),
`IUserMerge`, and the GDPR export/erasure bodies (all-year/all-status memberships and role assignments, with deletion for both slices) on `ICampManagement`; the registered `IUserDataContributor` is the decorator, which calls them. All caching lives in the decorator
— the inner service never touches `IMemoryCache`.

### CachingCampService (Singleton, `Humans.Camps.Services`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, CampInfo>` (`Camp.CampInfo`, warmed on startup) | Per-Entity | yes | yes | yes (its own write verbs, merge, GDPR erasure and role writes; every eviction is a `RefreshAll`) |
| `CampSettingsInfo` single slot (no `IMemoryCache`) | Static | yes | yes | yes (its own settings write verbs) |

Implements `ICampManagement` (hence `ICampService`), `ICampLeadDirectory`,
`ICampSeeding`, `ICampRoleCampAccess`, `IUserMerge`, `ICampRoleService`,
`IUserDataContributor`, `IEarlyEntryProvider`. The last three wrap writes
that change rows the projection serves, so the cache owner evicts on them
itself. `SearchAsync` is served from the cached `CampInfo`
snapshot, never the DB. Surfaced on `/Debug/CacheStats`.

### CampRoleService (Scoped — wrapped by CachingCampService)

Repository: `ICampRepository`.

| Table | R/W |
|-------|-----|
| CampRoleDefinitions | R/W |
| CampRoleAssignments | R/W |
| CampMembers | R (camp-member status via `ICampRoleCampAccess`) |
| Camps | R (via repo helper) |

Cross-section calls via `ICampRoleCampAccess` (implemented by
`CachingCampService` — camp-member status without the full camp surface),
`IUserService`, `IUserEmailService`, `IAuditLogService`,
`INotificationEmitter`. The decorator evicts the cached `CampInfo` after its
role writes. Implements
`IGoogleGroupMembershipSource` (camp-role Google group membership). No
direct `IMemoryCache`.

### CampContactService (Scoped)

No repository. Rate-limited contact relay. Injects `IEmailService`,
`IEmailMessageFactory`, `IAuditLogService`, `INotificationEmitter`,
`IUserService`, `IMemoryCache`, `ILogger`. **No `ICampManagement` injection**
— the controller resolves the camp/lead data and passes display names,
contact email, and lead user ids as parameters to `SendFacilitatedMessageAsync`.

| Cache Key | TTL | Type |
|-----------|-----|------|
| `CampContactRateLimit:{userId}:{campId}` | 10 min | Rate limit |

---


