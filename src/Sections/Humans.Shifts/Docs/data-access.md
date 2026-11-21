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

`ShiftViewService` builds the user views behind `IShiftView`, which
`CachingShiftService` serves.

### CachingShiftService (Singleton)

No repository. Decorates `IShiftManagement` (with `IShiftService`, `IShiftSeeding`,
`IShiftVolunteerProfiles`), `IShiftSignupService` (with `IShiftSignups`,
`IShiftSignupSeeding`), `IUserMerge` and `IUserDataContributor`; the inner
`ShiftManagementService`, `ShiftSignupService` and the merge inners are registered only
under the key `shift-inner`, so every Shifts write passes through it. It is the
section's only `IUserMerge`, running the three inner merges in their old order.

| Cache | Key | Value | Warm | Refresh |
|-------|-----|-------|------|---------|
| `Shifts.Rota` (`TrackedCache`) | rota id | `ShiftAdminRota`: rota, its shifts and every signup, all events | at startup, under the refresh lock | after every rota, shift or signup write, in `finally`, under the refresh lock; user-wide writes, merge, erasure and event deletion reload every rota |

Reads come from the inner `IShiftRotaRows` (`ShiftManagementService.GetRotaRowsAsync` →
`IShiftManagementRepository.GetRotaRowsAsync`). Browse, user views, ownership checks and the
staffing reports on `ShiftManagementService` read the cached rows; the other readers move onto `IShiftRotaRows` one area at a time.

### ShiftManagementService (Scoped, keyed inner of CachingShiftService)

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

Cross-section calls via `IAuditLogService`, `IAdminAuthorizationService`,
plus `IServiceProvider` for cycle-breaking, which
lazy-resolves the read surfaces `ITeamService`, `IUserService`,
`ICampService`, `IRoleAssignmentService`, and
`ITicketService`. No cache; dashboard figures are computed from the cached rota rows on
each request. `GetCoordinatorTeamIdsAsync` has no
cache of its own: Teams' singleton decorator answers it from RAM. Implements
`IUserMerge`. Also exposes the Cantina-gating predicates
(`HasQualifyingCantinaSignupAsync`, `GetOnSiteUserIdsForDayAsync`).

**Shift Summary by Camp.** `BuildSummaryAsync` assembles the
read-only by-camp shift summary: confirmed signup totals come from
the cached rota rows (`IShiftRotaRows`), scoped by `EventSettings` id,
camp labels and the active-camp roster from `ICampService`, and display
names from `IUserService` — stitched in memory at the service layer with
**no new cross-section interface** and no foreign-repository read. Returns
`null` for an unknown team-slug / out-of-scope rota (controller maps to 404).

### ShiftSignupService (Scoped, keyed inner of CachingShiftService)

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
`IUserService` (user lookups), plus `IServiceProvider`
(lazy-resolves `ITeamService` for coordinator/team-name lookups).
Holds the GDPR export and erasure that `CachingShiftService` exposes as `IUserDataContributor`. Implements `IUserMerge`, `ICalendarFeedContributor`
(personal iCal feed contributor — the user's Confirmed and Pending shift
signups; Cancelled/Bailed/NoShow history excluded). `EraseForUserAsync`
(GDPR Art. 17) cancels active signups, then deletes the user's volunteer
event profile, availability/build-status rows (via
`IVolunteerTrackingRepository.DeleteVolunteerTrackingForUserAsync`), and
shift-tag preferences. No `IMemoryCache`.

### ShiftSignupService notice delivery

These methods use no repository, `IMemoryCache` or table access. Post-save notice delivery for signups: the volunteer's
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

Cross-section calls via `IUserService`, `ITicketService` (`GetAllParticipationsAsync`), `IAuditLogService` (self-service availability saves). No
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

### ShiftViewService (Scoped, keyed inner of CachingShiftService)

Repositories: `IShiftManagementRepository`, `IVolunteerTrackingRepository`. User-view signups come from the
rota cache (`IShiftRotaRows`), not a repository read.

| Table | R/W | Repo |
|-------|-----|------|
| EventSettings | R | IShiftManagementRepository |
| Rotas | R | IShiftManagementRepository |
| Shifts | R | IShiftManagementRepository |
| VolunteerEventProfiles | R | IShiftManagementRepository |
| ShiftTags | R | IShiftManagementRepository |
| VolunteerTagPreferences | R | IShiftManagementRepository |
| GeneralAvailability | R | IVolunteerTrackingRepository |
| VolunteerBuildStatuses | R | IVolunteerTrackingRepository |

Implements the section-internal `IShiftRowView`: assembles user views from two
repositories plus the cached rota rows. Keyed `"shift-inner"`; `CachingShiftService`
resolves it per call in a new scope. Nothing here is cached.


### BurnSettingsService (Scoped)

No repository, no tables. Reads through the section's `EventCalendarResolver`, which composes
`ISettingsService`'s event dates with Shifts' own build sub-periods.

Read-only adapter exposing the composed `BurnSettingsInfo` DTO to the
section's own pages. Section-internal. No cache (single active row, cold path).

### RotaCoordinatorMessageService (Scoped)

No repository: rotas, shifts and signups come from the cached rota rows (`IShiftRotaRows`);
the active event is resolved via `EventCalendarResolver`.

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

No repository: shifts and signups come from the cached rota rows (`IShiftRotaRows`), the
event from `EventCalendarResolver`. Cross-section calls via `ITeamService` (team
projections for role-period estimates), `IUserService`. No cache of its own.

### EarlyEntryCapacityCalculator / ShiftEarlyEntryProjection / TeamPalette

Stateless calculators / projections — no DI dependencies, no DB access.

---



### ShiftManagementService browse reads

Repository: `IShiftManagementRepository`. Assembles urgent/general browse rows
through the existing event calendar resolver, urgency ranking and department
aggregation helpers. Resolves Teams, Users and Auth lazily through the existing
service provider; owns unchanged coordinator/approval read predicates used by
browse eligibility. Shared shift/rota projection also serves existing management
reads. These read methods reuse the service’s dependencies and registration and add no cache.

### ShiftManagementService dashboard metrics

Repository: `IShiftManagementRepository` (event `CreatedAt` only). Computes the dashboard overview,
trends and post-event figures from the cached rota rows (`IShiftRotaRows`). These read methods reuse the service’s dependencies and registration and add no cache.

| Table | R/W |
|-------|-----|
| EventSettings | R |

Cross-section reads, lazy-resolved through `IServiceProvider`: `ITeamService`, `ITicketService`,
`IUserService`; `IClock` for the windows.

### ShiftManagementService coordinator activity

No repository: computes the per-coordinator activity rows for the dashboard from the cached rota
rows (`IShiftRotaRows`). These read methods reuse the service’s dependencies and registration and add no cache.

Cross-section reads, lazy-resolved through `IServiceProvider`: `ITeamService` (coordinators),
`IUserService` (display names).

### ShiftManagementService coverage reports

No repository: staffing snapshot and department coverage figures from the cached rota rows
(`IShiftRotaRows`). These read methods reuse the service’s dependencies and registration and add no cache.

Cross-section reads via `ITeamService`, lazy-resolved through `IServiceProvider`.

### ShiftManagementService camp summary

No repository: the read-only shift summary by camp. Confirmed signup totals come from the cached
rota rows (`IShiftRotaRows`); camp labels and the active-camp roster from `ICampService`, display
names from `IUserService`, team scoping from `ITeamService`, all lazy-resolved through
`IServiceProvider` and stitched in memory. These read methods reuse the service’s dependencies and registration and add no cache.
| Rotas | R |
| EventSettings | R |

### ShiftSignupService personal-data reads

Repositories: `IShiftManagementRepository`, `IVolunteerTrackingRepository`. Builds the member's GDPR
export slices (`ShiftSignups`, `VolunteerEventProfiles`, `GeneralAvailability`,
`VolunteerBuildStatuses`, `ShiftTagPreferences`) behind `ShiftSignupService`'s
`IUserDataContributor`. These projections are read-only and reuse the service’s existing registration and dependencies; no cache.

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
