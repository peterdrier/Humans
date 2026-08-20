# Shifts — Data Access

## Shifts

Folder: `src/Sections/Humans.Shifts/Services/` — repository under `Data/`.
**DbContext:** `ShiftsDbContext`. `EventSettings` stays with Shifts, not
`EventGuideDbContext`, despite the Events/EventGuide section's
similarly-named tables — see `EventGuideDbContext`'s doc comment.
`ShiftRepository` / `VolunteerTrackingRepository` inject
`IDbContextFactory<ShiftsDbContext>` directly. Owns `Rotas`, `Shifts`,
`ShiftSignups`, `EventSettings`, `GeneralAvailability`,
`VolunteerEventProfiles`, `VolunteerBuildStatuses`, `ShiftTags`,
`VolunteerTagPreferences`. `EventParticipations` lives on `UsersDbContext`
under the Users section, not here.

`IShiftManagementRepository` and `IShiftSignupRepository` are both backed
by one concrete partial class `ShiftRepository` (`.Management.cs` +
`.Signups.cs` partials). `GetEligibleBuildSignupsAsync` and
`GetConfirmedShiftsInRangeAsync` (reading `ShiftSignups` + `EventSettings`)
live on `ShiftRepository`'s `.Signups.cs` partial, surfaced on
`IShiftManagementRepository`. `VolunteerTrackingRepository` owns only
`VolunteerBuildStatuses` and `GeneralAvailability`.

`ShiftViewService` provides the inner
implementation of `IShiftView`; it is wrapped by
`Humans.Shifts.Services.CachingShiftViewService`
(Singleton decorator with two `TrackedCache` dictionaries for user and
rota views).

### ShiftManagementService (Scoped)

Repository: `IShiftManagementRepository`.

| Table | R/W |
|-------|-----|
| Rotas | R/W |
| Shifts | R/W |
| ShiftSignups | R |
| EventSettings | R/W |
| VolunteerEventProfiles | R/W |
| ShiftTags | R/W |
| VolunteerTagPreferences | R/W |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `shift-auth:{userId}` | 60 sec | yes | yes | yes (also via `IShiftAuthorizationInvalidator`) |
| `dashboard-overall-coverage` | 5 min | yes | no | yes (via `EvictDashboardCaches`, on EventSettings change) |

Cross-section calls via `IAuditLogService`, `IAdminAuthorizationService`,
`IShiftViewInvalidator`, plus `IServiceProvider` for cycle-breaking, which
lazy-resolves the read surfaces `ITeamServiceRead`, `IUserServiceRead`,
`ICampServiceRead`, `IRoleAssignmentService`, and
`ITicketServiceRead`. Injects `IMemoryCache` directly for the
`shift-auth:{userId}` slot. Implements `IShiftAuthorizationInvalidator`,
`IUserMerge`. Also exposes the Cantina-gating predicates
(`HasQualifyingCantinaSignupAsync`, `GetOnSiteUserIdsForDayAsync`).

**Shift Summary by Camp.** `BuildSummaryAsync` assembles the
read-only by-camp shift summary: confirmed signup totals come from
`IShiftManagementRepository.GetConfirmedUserShiftTotalsAsync` (reads
`ShiftSignups` joined to `Shifts`/`Rotas`, scoped by `EventSettings` id),
camp labels and the active-camp roster from `ICampServiceRead`, and display
names from `IUserServiceRead` — stitched in memory at the service layer with
**no new cross-section interface** and no foreign-repository read. Returns
`null` for an unknown team-slug / out-of-scope rota (controller maps to 404).

### ShiftSignupService (Scoped)

Repositories: `IShiftManagementRepository`, `IVolunteerTrackingRepository`.

| Table | R/W |
|-------|-----|
| ShiftSignups | R/W |
| Shifts | R (via repo) |
| Rotas | R (via repo) |
| VolunteerEventProfiles | R/W (via repo) |
| VolunteerTagPreferences | R (via repo) |
| GeneralAvailability | R (via `IVolunteerTrackingRepository`, GDPR export) |

Cross-section calls via `IShiftManagementService`, `IBurnSettingsService`,
`IAuditLogService`, `INotificationEmitter`, `IAdminAuthorizationService`,
`IShiftViewInvalidator`, `IEarlyEntryInvalidator`, plus `IServiceProvider`
(lazy-resolves `ITeamServiceRead` for coordinator/team-name lookups).
Implements `IUserDataContributor`, `IUserMerge`, `ICalendarFeedContributor`
(personal iCal feed contributor — the user's Confirmed and Pending shift
signups; Cancelled/Bailed/NoShow history excluded). No `IMemoryCache`.

### VolunteerTrackingService (Scoped)

Repositories: `IVolunteerTrackingRepository`, `IShiftManagementRepository`.

| Table | R/W | Repo |
|-------|-----|------|
| VolunteerBuildStatuses | R/W | IVolunteerTrackingRepository |
| GeneralAvailability | R/W | IVolunteerTrackingRepository (availability upsert, set/clear day-off, camp-setup) |
| ShiftSignups | R | IShiftManagementRepository (`GetEligibleBuildSignupsAsync`) |
| EventSettings | R | IShiftManagementRepository (`GetEligibleBuildSignupsAsync` / `GetActiveEventSettingsAsync`) |
| Shifts | R | IShiftManagementRepository |
| Rotas | R | IShiftManagementRepository |

Cross-section calls via `IUserServiceRead`, `IShiftViewInvalidator`. No
cache. Holds the gap-detection algorithm + heatmap data assembly,
plus the full day-availability / camp-setup / day-off mutation surface.
Build-eligibility signups (`ShiftSignups` + `EventSettings`) read through
`IShiftManagementRepository.GetEligibleBuildSignupsAsync`. Implements
`IVolunteerTrackingService`, `IUserMerge`.

### VolunteerTrackingExportService (Scoped)

Repository: `IShiftManagementRepository`.

| Table | R/W |
|-------|-----|
| ShiftSignups | R (`GetConfirmedShiftsInRangeAsync` — confirmed shifts in range) |
| EventSettings | R (range/zone resolution inside `GetConfirmedShiftsInRangeAsync`) |
| Shifts | R (via repo) |
| Rotas | R (via repo) |

Cross-section calls via `IShiftManagementService`, `IUserServiceRead`.
Implements `IVolunteerTrackingExportService`, `IEarlyEntryProvider`
(contributes confirmed-build-shift early-entry rows). No cache, no
direct DB access beyond the export query.

### ShiftViewService (Scoped — wrapped by CachingShiftViewService Singleton decorator)

Repositories: `IShiftManagementRepository`, `IVolunteerTrackingRepository`.

| Table | R/W | Repo |
|-------|-----|------|
| EventSettings | R | IShiftManagementRepository |
| Rotas | R | IShiftManagementRepository |
| Shifts | R | IShiftManagementRepository |
| ShiftSignups | R | IShiftManagementRepository (via `GetForUsersAsync` / rota view signups) |
| VolunteerEventProfiles | R | IShiftManagementRepository |
| ShiftTags | R | IShiftManagementRepository |
| VolunteerTagPreferences | R | IShiftManagementRepository |
| GeneralAvailability | R | IVolunteerTrackingRepository |
| VolunteerBuildStatuses | R | IVolunteerTrackingRepository |

Implements `IShiftView`. Pure read assembler — composes user + rota
views from two repositories. Wrapped by `CachingShiftViewService`
which caches both projection types per-entity (per-user view and
per-rota view). Service-keyed as `"shift-view-inner"` so the decorator
can resolve it without self-recursion.

### CachingShiftViewService (Singleton, `Humans.Shifts.Services`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, ShiftUserView>` (`ShiftView.UserView`, in-process, no `IMemoryCache`) | Per-User | yes | yes | yes (via `IShiftViewInvalidator.InvalidateUser`) |
| `TrackedCache<Guid, ShiftRotaView>` (`ShiftView.RotaView`, in-process, no `IMemoryCache`) | Per-Entity | yes | yes | yes (via `IShiftViewInvalidator.InvalidateRota`) |

Implements `IShiftView`, `IShiftViewInvalidator`. Resolves the inner
Scoped `IShiftView` via `IServiceScopeFactory` to honour scope rules.
Both cache instances are surfaced on `/Debug/CacheStats`.

### BurnSettingsService (Scoped)

Repository: `IShiftManagementRepository` (read-only — fetches `EventSettings`).

| Table | R/W |
|-------|-----|
| EventSettings | R |

Read-only adapter mapping `EventSettings` → `BurnSettingsInfo` DTO at the
section boundary. Exposes `IBurnSettingsService` for cross-section
consumers that need active-event metadata without coupling to the full
shifts surface. No cache (single active row, cold path).

### RotaCoordinatorMessageService (Scoped)

Repository: `IShiftManagementRepository`.

| Table | R/W |
|-------|-----|
| ShiftSignups | R (loaded via `GetRotaAsync(RotaReadShape.View)`) |
| Rotas | R |
| Shifts | R |
| EventSettings | R (team-level dispatch path — `GetActiveEventSettingsAsync`) |

Cross-section calls via `ITeamServiceRead`, `IUserServiceRead`,
`IEmailService`, `IEmailMessageFactory`, `IAuditLogService`. Implements
per-rota (`SendRotaMessageAsync`) and team-level
(`SendTeamRotasMessageAsync`) dispatch — groups active signups
by user across one or many rotas and enqueues one personalised email per
recipient via the outbox. No cache.

### WorkloadService (Scoped) — `Shifts/Workload/`

Repository: `IShiftManagementRepository`.

| Table | R/W |
|-------|-----|
| EventSettings | R |
| Shifts | R (via repo) |
| Rotas | R (cached via `IShiftView`) |

Cross-section calls via `IShiftView` (cached), `ITeamService` (team
projections for role-period estimates), `IUserServiceRead`. No own
cache — relies on the per-rota `ShiftView.RotaView` eviction to refresh.

### EarlyEntryCapacityCalculator / ShiftEarlyEntryProjection / TeamPalette

Stateless calculators / projections — no DI dependencies, no DB access.

---


