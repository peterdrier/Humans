# Calendar — Data Access

## Calendar

Project: `src/Sections/Humans.Calendar` — services under `Services/`,
repository under `Data/`. **DbContext:** `CalendarDbContext`.
`CalendarRepository` injects `IDbContextFactory<CalendarDbContext>`
directly. Owns `CalendarEvents`, `CalendarEventExceptions`,
`CalendarFeedTokens`. The inner
`ICalendarService` is wrapped by
`Humans.Calendar.Services.CachingCalendarService` (Singleton decorator
inheriting `TrackedCache<Guid, CalendarEventInfo>`, warmed on startup).
The decorator exposes the cross-section read surface as
`ICalendarServiceRead` and is its only implementation — it answers them
from its snapshot, so the inner does not implement that interface at all. Writes
delegate to the inner service then refresh the affected event row. `CalendarRepository` does not join the `Teams`
table — the decorator stitches team names via `ITeamServiceRead` at the
application layer.

### CalendarService (Scoped — wrapped by CachingCalendarService Singleton decorator)

Repository: `ICalendarRepository`.

| Table | R/W |
|-------|-----|
| CalendarEvents | R/W |
| CalendarEventExceptions | R/W |

Cross-section calls via `IAuditLogService`. Team names are stitched by the
decorator (`ITeamServiceRead`), on the read path the inner does not serve.
Recurrence expansion is in-section and static (`CalendarOccurrenceExpander`),
not an injected dependency.

### CachingCalendarService (Singleton, `Humans.Calendar.Services`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, CalendarEventInfo>` (`Calendar.Event`, warmed on startup) | Per-Entity | yes | yes | yes (per-event `ReplaceAsync` after each delegated write) |

Implements `ICalendarService`, `ICalendarServiceRead`. Resolves the keyed
Scoped inner per-call; resolves `ITeamServiceRead` for occurrence team
names, and (when `teamId` is null) `IEnumerable<ICalendarFeedContributor>`
for community-calendar items — fanned out fresh per call, never cached, and
never routed through the repository. Surfaced on `/Debug/CacheStats`.

---


## ICalFeed

Folder: `src/Sections/Humans.Calendar/Services/`, with `ICalendarFeedContributor`,
`CalendarFeedItem` and `IICalFeedService` under `Humans.Calendar/Contracts/`
(the feed is Calendar-owned, not a section of its own). Personal iCal feed
orchestrator. Owns no DB tables; fans
out over `IEnumerable<ICalendarFeedContributor>` implementations registered by other
sections. Requires a valid token in this section's own `CalendarFeedTokens`
table, reached through the internal `ICalendarFeedTokenService`.

### ICalFeedService (Scoped)

No repository. Injects `ICalendarFeedTokenService` (token validation),
`IUserServiceRead` (the user guard — a missing or merged user is a 404 before
the token is ever compared) and `IEnumerable<ICalendarFeedContributor>`.

| Table | R/W |
|-------|-----|
| _(none — token check via `ICalendarFeedTokenService`, no direct DB access)_ | — |

### CalendarFeedTokenService (Singleton, `Humans.Calendar.Services`)

Repository: `ICalendarRepository`. `EnsureAsync` is a single
`GetOrAddFeedTokenAsync` round trip rather than a read then a write, so a racing
first view loses on the primary key and adopts the winner's token instead of
500ing; `RotateAsync` keeps the plain upsert, where last write wins. Internal:
the token's whole lifecycle stays inside the section, and the only part that leaves it is
`IICalFeedService.HasFeedAsync` (a bool for the admin widget, never the token).

| Table | R/W |
|-------|-----|
| CalendarFeedTokens | R/W |

Also the section's `IUserDataContributor` (GDPR export slice
`CalendarFeedToken` — feed existence only, never the secret — plus the erasure
path) and its `IUserMerge` (deletes the eliminated account's row; the survivor
keeps their own token and the dead account's feed URL stops working).

Current `ICalendarFeedContributor` implementations (registered by their owning
sections in each section's own `Section.cs`):

- **`ShiftSignupService`** (Shifts) — the user's Confirmed **and** Pending shift signups (pending get a "(pending)" summary suffix); Cancelled/Bailed/NoShow history is excluded. `GetPublicItemsForWindowAsync` returns `[]` — nothing public yet.
- **`EventService`** (Events) — approved event-guide entries the user has favourited (moderation un-approval drops an event from the feed without touching the favourite row). No hosting/ownership path. `GetPublicItemsForWindowAsync` returns `[]` — nothing public yet.
- **`WorkgroupCalendarContributor`** (Workgroups) — meetings of the active workgroups the user belongs to; dormant groups are excluded. `GetPublicItemsForWindowAsync` returns the `IsPublic` meetings of active workgroups that overlap the window.

`GetPublicItemsForWindowAsync` results are consumed by `CachingCalendarService.GetOccurrencesInWindowAsync`
(Calendar section, above), not by `ICalFeedService` — the two fan-outs share the
interface and contributor registrations but serve different surfaces (personal
feed vs. community calendar).

Sequential fan-out, matching `GdprService` and `EarlyEntryService`.
`ShiftSignupService` reads via `ShiftsDbContext` and `EventService` via
`EventGuideDbContext`, each from its own `IDbContextFactory`; independent
factory-created contexts *can* safely run concurrently (EF's restriction is
on concurrent operations against the **same** context instance). The
fan-out is kept sequential for consistency with the other contributor
orchestrators, not because parallelism would be unsafe. No
`IMemoryCache` — the section's DB reads are contributor-owned; the user-info
token check comes from the warm `CachingUserService` TrackedCache.

---


