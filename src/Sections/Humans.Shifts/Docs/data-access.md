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
`VolunteerTagPreferences`. Event participation lives on `TicketsDbContext`
under the Tickets section (read through `ITicketService`), not here.

`IShiftManagementRepository` (declared across `IShiftManagementRepository.cs` and
`IShiftManagementRepository.Signups.cs`) is backed by one concrete partial class
`ShiftRepository` (`.Management.cs` + `.Signups.cs` partials). `GetEligibleBuildSignupsAsync` and
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
| `dashboard-overview:{eventId}:{period}` (`OverviewCacheKey`) | 5 min sliding | yes | yes | yes (via `EvictDashboardCaches`) |
| `dashboard-coordinator-activity:{eventId}:{period}` (`CoordinatorActivityCacheKey`) | 5 min sliding | yes | yes | yes (via `EvictDashboardCaches`) |
| `dashboard-trends:{eventId}:{window}:{period}` (`TrendsCacheKey`) | 5 min sliding | yes | yes | yes (via `EvictDashboardCaches`) |
| `dashboard-overall-coverage` | 5 min sliding | yes | yes | yes (via `EvictDashboardCaches`, on EventSettings change) |

Cross-section calls via `IAuditLogService`, `IAdminAuthorizationService`,
plus `IServiceProvider` for cycle-breaking, which
lazy-resolves the read surfaces `ITeamService`, `IUserService`,
`ICampService`, `IRoleAssignmentService`, and
`ITicketService`. Evicts the view cache through `CachingShiftViewService`. Injects
`IMemoryCache` directly for the dashboard slots. `GetCoordinatorTeamIdsAsync` has no
cache of its own: Teams' singleton decorator answers it from RAM. Implements
`IUserMerge`. Also exposes the Cantina-gating predicates
(`HasQualifyingCantinaSignupAsync`, `GetOnSiteUserIdsForDayAsync`).

**Shift Summary by Camp.** `BuildSummaryAsync` assembles the
read-only by-camp shift summary: confirmed signup totals come from
`IShiftManagementRepository.GetConfirmedUserShiftTotalsAsync` (reads
`ShiftSignups` joined to `Shifts`/`Rotas`, scoped by `EventSettings` id),
camp labels and the active-camp roster from `ICampService`, and display
names from `IUserService` — stitched in memory at the service layer with
**no new cross-section interface** and no foreign-repository read. Returns
`null` for an unknown team-slug / out-of-scope rota (controller maps to 404).

### ShiftSignupService (Scoped)

Repositories: `IShiftManagementRepository`, `IVolunteerTrackingRepository`.

| Table | R/W |
|-------|-----|
| ShiftSignups | R/W |
| Shifts | R (via repo) |
| Rotas | R (via repo) |
| VolunteerEventProfiles | R/W (via repo, incl. GDPR erasure) |
| VolunteerTagPreferences | R/W (via repo, incl. GDPR erasure) |
| GeneralAvailability | R/W (via `IVolunteerTrackingRepository`; R for GDPR export, W for GDPR erasure) |
| VolunteerBuildStatuses | R/W (via `IVolunteerTrackingRepository`; R for GDPR export, W for GDPR erasure) |

Cross-section calls via `IShiftManagement`, `ISettingsService` (via
`EventCalendarResolver`, nobodies-collective/Humans#1630),
`IAuditLogService`, `INotificationEmitter`, `IAdminAuthorizationService`,
`IUserService` (user lookups), `CachingShiftViewService`, plus `IServiceProvider`
(lazy-resolves `ITeamService` for coordinator/team-name lookups).
Implements `IUserDataContributor`, `IUserMerge`, `ICalendarFeedContributor`
(personal iCal feed contributor — the user's Confirmed and Pending shift
signups; Cancelled/Bailed/NoShow history excluded). `EraseForUserAsync`
(GDPR Art. 17) cancels active signups, then deletes the user's volunteer
event profile, availability/build-status rows (via
`IVolunteerTrackingRepository.DeleteVolunteerTrackingForUserAsync`), and
shift-tag preferences. No `IMemoryCache`.

### ShiftSignupNotices (constructed by ShiftSignupService, not DI-registered)

No repository, no `IMemoryCache`, no table access. Post-save notice delivery for signups: the volunteer's
assignment notice and the team coordinators' coverage-gap and signup-change notices. A lookup or channel failure
is logged and never changes the saved signup outcome. Cross-section calls via `INotificationEmitter`,
`IUserService` (recipient culture), `EventCalendarResolver` (gate-opening date, via `ISettingsService`), and
`ITeamService` (coordinator ids, lazy-resolved through `IServiceProvider` to keep the TeamService constructor
cycle open).

### VolunteerTrackingService (Scoped)

Repositories: `IVolunteerTrackingRepository`, `IShiftManagementRepository`.

| Table | R/W | Repo |
|-------|-----|------|
| VolunteerBuildStatuses | R/W | IVolunteerTrackingRepository |
| GeneralAvailability | R/W | IVolunteerTrackingRepository (availability upsert, set/clear day-off, camp-setup) |
| ShiftSignups | R | IShiftManagementRepository (`GetEligibleBuildSignupsAsync`) |
| EventSettings | R | IShiftManagementRepository (`GetEligibleBuildSignupsAsync`); active event resolved via `EventCalendarResolver` (Settings), not this repo |
| Shifts | R | IShiftManagementRepository |
| Rotas | R | IShiftManagementRepository |

Cross-section calls via `IUserService`, `ITicketService` (`GetAllParticipationsAsync`), `IAuditLogService` (self-service availability saves); evicts through `CachingShiftViewService`. No
cache. Holds the gap-detection algorithm + heatmap data assembly,
plus the full day-availability / camp-setup / day-off mutation surface.
Build-eligibility signups (`ShiftSignups` + `EventSettings`) read through
`IShiftManagementRepository.GetEligibleBuildSignupsAsync`. Implements
`IVolunteerTrackingManagement`, `IUserMerge`.

### VolunteerTrackingExportService (Scoped)

Repository: `IShiftManagementRepository`.

| Table | R/W |
|-------|-----|
| ShiftSignups | R (`GetConfirmedShiftsInRangeAsync` — confirmed shifts in range) |
| EventSettings | R (range/zone resolution inside `GetConfirmedShiftsInRangeAsync`) |
| Shifts | R (via repo) |
| Rotas | R (via repo) |

Cross-section calls via `IShiftManagement`, `IUserService`, `IAuditLogService` (one entry per
export). Reads `IBurnSettingsService` (section-internal).
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

Implements the section-internal `IShiftRowView`: assembles user + rota
views from two repositories and forwards Settings change callbacks to
`IShiftManagement.InvalidateDashboardCaches` for event-keyed dashboard eviction. Wrapped by `CachingShiftViewService`
which caches both projection types per-entity (per-user view and
per-rota view). Service-keyed as `"shift-view-inner"` so the decorator
can resolve it without self-recursion.

### CachingShiftViewService (Singleton, `Humans.Shifts.Services`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, ShiftUserView>` (`ShiftView.UserView`, in-process, no `IMemoryCache`) | Per-User | yes | yes | yes (`InvalidateUser`, called by the section's own writes) |
| `TrackedCache<Guid, ShiftRotaView>` (`ShiftView.RotaView`, in-process, no `IMemoryCache`) | Per-Entity | yes | yes | yes (`InvalidateRota`, called by the section's own writes) |

Implements `IShiftView`; its `Invalidate*` methods are called only by Shifts' own
services, so nothing outside the section evicts it. Resolves the inner
Scoped `IShiftRowView` via `IServiceScopeFactory` to honour scope rules.
Settings change callbacks clear both view caches and synchronously delegate to that
same keyed inner; the decorator resolves no other service.
Both cache instances are surfaced on `/Debug/CacheStats`.

### BurnSettingsService (Scoped)

No repository, no tables. Reads through the section's `EventCalendarResolver`, which composes
`ISettingsService`'s event dates with Shifts' own build sub-periods.

Read-only adapter exposing the composed `BurnSettingsInfo` DTO at the
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
| EventSettings | R (team-level dispatch path — active event resolved via `EventCalendarResolver`, not this repo) |

Cross-section calls via `ITeamService`, `IUserService`,
`IEmailService`, `IAuditLogService`; builds its own messages through the
section's `ShiftsEmails`. Implements
per-rota (`SendRotaMessageAsync`) and team-level
(`SendTeamRotasMessageAsync`) dispatch — groups active signups
by user across one or many rotas and enqueues one personalised email per
recipient via the outbox. No cache.

### ShiftsEmails / ShiftsEmailPreviews (Scoped, internal)

No repository, no DB access, no cache. `ShiftsEmails` builds the section's
coordinator rota / team-rotas messages; `ShiftsEmailPreviews` is the section's
`IEmailPreviewContributor` for `/Email/EmailPreview`, one sample per template
built through `ShiftsEmails`.

### WorkloadService (Scoped) — `Shifts/Workload/`

Repository: `IShiftManagementRepository`.

| Table | R/W |
|-------|-----|
| EventSettings | R |
| Shifts | R (via repo) |
| Rotas | R (cached via the section-internal `IShiftRowView`) |

Section-internal `IShiftRowView` (cached) and `EventCalendarResolver`. Cross-section
calls via `ITeamService` (team
projections for role-period estimates), `IUserService`. No own
cache — relies on the per-rota `ShiftView.RotaView` eviction to refresh.

### EarlyEntryCapacityCalculator / ShiftEarlyEntryProjection / TeamPalette

Stateless calculators / projections — no DI dependencies, no DB access.

---



### ShiftBrowseReader (internal, owned by ShiftManagementService)

Repository: `IShiftManagementRepository`. Assembles urgent/general browse rows
through the existing event calendar resolver, urgency ranking and department
aggregation helpers. Resolves Teams, Users and Auth lazily through the existing
service provider; owns unchanged coordinator/approval read predicates used by
browse eligibility. Shared shift/rota projection also serves existing management
reads. No writes, cache or separate DI registration.

### ShiftDashboardMetrics (internal, owned by ShiftManagementService)

Repository: `IShiftManagementRepository`. Computes the dashboard overview, trends and stale-pending
figures. Read-only; no DI registration. `ShiftManagementService` caches its results; this class holds
no `IMemoryCache`.

| Table | R/W |
|-------|-----|
| Shifts | R |
| Rotas | R |
| ShiftSignups | R (status counts, confirmed counts per shift, created-at window, stale pending) |
| EventSettings | R |

Cross-section reads, lazy-resolved through `IServiceProvider`: `ITeamService`, `ITicketService`,
`IUserService`; `IClock` for the windows.

### ShiftCoordinatorActivity (internal, owned by ShiftManagementService)

Repository: `IShiftManagementRepository`. Computes the per-coordinator activity rows for the
dashboard. Read-only; no DI registration, no cache of its own (the owner caches under
`dashboard-coordinator-activity:{eventId}:{period}`).

| Table | R/W |
|-------|-----|
| Rotas | R |
| Shifts | R |
| ShiftSignups | R (pending counts per team) |

Cross-section reads, lazy-resolved through `IServiceProvider`: `ITeamService` (coordinators),
`IUserService` (display names).

### ShiftCoverageReports (internal, owned by ShiftManagementService)

Repository: `IShiftManagementRepository`. Staffing snapshot and department coverage figures.
Read-only; no DI registration, no cache of its own (the owner caches
`dashboard-overall-coverage`).

| Table | R/W |
|-------|-----|
| Rotas | R |
| Shifts | R |
| ShiftSignups | R (confirmed counts per shift) |
| EventSettings | R |

Cross-section reads via `ITeamService`, lazy-resolved through `IServiceProvider`.

### ShiftCampSummary (internal, owned by ShiftManagementService)

Repository: `IShiftManagementRepository`. The read-only shift summary by camp. Confirmed signup
totals come from `GetConfirmedUserShiftTotalsAsync`; camp labels and the active-camp roster from
`ICampService`, display names from `IUserService`, team scoping from `ITeamService` — all lazy-resolved
through `IServiceProvider` and stitched in memory. No DI registration, no cache.

| Table | R/W |
|-------|-----|
| ShiftSignups | R (confirmed totals per user) |
| Shifts | R |
| Rotas | R |
| EventSettings | R |

### ShiftSignupPersonalData (internal, owned by ShiftSignupService)

Repositories: `IShiftManagementRepository`, `IVolunteerTrackingRepository`. Builds the member's GDPR
export slices (`ShiftSignups`, `VolunteerEventProfiles`, `GeneralAvailability`,
`VolunteerBuildStatuses`, `ShiftTagPreferences`) behind `ShiftSignupService`'s
`IUserDataContributor`. Read-only; no DI registration, no cache.

| Table | R/W |
|-------|-----|
| ShiftSignups | R (per account in the merge chain, with shift, rota and event settings) |
| EventSettings | R |
| VolunteerEventProfiles | R |
| VolunteerTagPreferences | R |
| GeneralAvailability | R (via `IVolunteerTrackingRepository`) |
| VolunteerBuildStatuses | R (via `IVolunteerTrackingRepository`) |

Cross-section calls via `IUserService` (merge chain), `ITeamService` (lazy through
`IServiceProvider`) and `EventCalendarResolver` (Settings).
