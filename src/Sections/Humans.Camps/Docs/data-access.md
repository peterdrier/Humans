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

`ICampRoleRepository` is consolidated into `ICampRepository` via a
`.Roles.cs` partial; `CampRoleService` injects `ICampRepository` directly.
The Camps section is a single repository owning all of its tables.
`CampService` implements `IEarlyEntryProvider` directly (no standalone
early-entry projection helper). `CampRoleService` does not inject the full
`ICampService`; it takes the narrow intra-section `ICampRoleCampAccess`
(implemented by `CampService`) for camp-member status lookups, plus
`ICampInfoInvalidator` to evict the cached `CampInfo` on role-assignment
writes.

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
| `Camp.CampInfo` TrackedCache + settings slot (`ICampInfoInvalidator`) | yes |
| `NavBadge:CampLeadJoinRequests:{userId}` (`ICampLeadJoinRequestsBadgeCacheInvalidator`) | yes |

| Cache (via invalidators) — cont. | Invalidate |
|-------------------------|------------|
| `EarlyEntry.UserEarlyEntry` TrackedCache (`IEarlyEntryInvalidator`) | yes (camp-lead grant changes) |

Cross-section calls via `IUserServiceRead`, `IAuditLogService`,
`ISystemTeamSync`, `IFileStorage`, `INotificationEmitter`,
`IEarlyEntryInvalidator`, plus `Lazy<ICampRoleService>` to break a DI
cycle. Implements `ICampRoleCampAccess` (narrow intra-section surface
consumed by `CampRoleService`), `IUserDataContributor`, `IUserMerge`,
`IEarlyEntryProvider`. The inner service no longer touches `IMemoryCache`
directly — all caching lives in the decorator.

### CachingCampService (Singleton, `Humans.Camps.Services`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, CampInfo>` (`Camp.CampInfo`, warmed on startup) | Per-Entity | yes | yes | yes (`ICampInfoInvalidator.InvalidateCampAsync`, wholesale `RefreshAll` for cross-cutting writes) |
| `CampSettingsInfo` single slot (no `IMemoryCache`) | Static | yes | yes | yes (`ICampInfoInvalidator.InvalidateSettingsAsync`) |

Implements `ICampService`, `ICampServiceRead`, `IUserMerge`,
`ICampInfoInvalidator`. `SearchAsync` is served from the cached `CampInfo`
snapshot, never the DB. Surfaced on `/Debug/CacheStats`.

### CampRoleService (Scoped)

Repository: `ICampRepository`.

| Table | R/W |
|-------|-----|
| CampRoleDefinitions | R/W |
| CampRoleAssignments | R/W |
| CampMembers | R (camp-member status via `ICampRoleCampAccess`) |
| Camps | R (via repo helper) |

| Cache (via invalidators) | Invalidate |
|-------------------------|------------|
| `Camp.CampInfo` TrackedCache (`ICampInfoInvalidator.InvalidateSeasonAsync`) | yes (role-assignment writes) |

Cross-section calls via `ICampRoleCampAccess` (implemented by
`CampService` — camp-member status without the full camp surface),
`IUserServiceRead`, `IUserEmailService`, `IAuditLogService`,
`INotificationEmitter`, plus `ICampInfoInvalidator` to evict the cached
`CampInfo` after role-assignment writes. Implements
`IGoogleGroupMembershipSource` (camp-role Google group membership). No
direct `IMemoryCache`.

### CampContactService (Scoped)

No repository. Rate-limited contact relay. Injects `IEmailService`,
`IEmailMessageFactory`, `IAuditLogService`, `INotificationEmitter`,
`IMemoryCache`, `ILogger`. **No `IUserService` or `ICampService` injection**
— the controller resolves the camp/lead data and passes display names,
contact email, and lead user ids as parameters to `SendFacilitatedMessageAsync`.
No cross-section service-to-service calls within the service itself.

| Cache Key | TTL | Type |
|-----------|-----|------|
| `CampContactRateLimit:{userId}:{campId}` | 10 min | Rate limit |

---


