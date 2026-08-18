# Events — Data Access

## Events (Event Guide)

Folder: `src/Sections/Humans.Events/Services/` (namespace
`Humans.Events.Services`). **DbContext:**
`EventGuideDbContext`.
`EventRepository` (`src/Sections/Humans.Events/Data/Repository.cs`)
injects `IDbContextFactory<EventGuideDbContext>`
directly. Owns `Events`,
`EventGuideSettings`, `EventCategories`, `EventVenues`,
`EventModerationActions`, `EventPreferences`, `EventFavourites`. The
name-colliding `EventSettings` table is **Shifts-owned** (lives on
`ShiftsDbContext` — see [Shifts](../../Humans.Shifts/Docs/data-access.md)) and
`EventParticipations` is **Users-owned** (lives on `UsersDbContext` —
see [Users](../../Humans.Users/Docs/data-access.md)); `EventGuideDbContext`
deliberately excludes both
(see its doc comment).

`EventRepository` does not read `EventSettings` (the Shifts-owned table)
directly — active-event discovery goes through a service-layer call.
`EventSettings` lives in a different DbContext (`ShiftsDbContext`) than
`EventRepository`'s `EventGuideDbContext`, so a direct EF read across the
two is not possible without a second context injection. The inner
`IEventService` is wrapped by `Humans.Events.Services.CachingEventService`
(Singleton decorator). It owns four split projections — a per-event
`TrackedCache<Guid, ApprovedEventView>` (`Event.ApprovedEventView`) plus
flat snapshots for categories, venues, and the guide-settings singleton.
Writes delegate to the inner service then invalidate the affected slice
inline (no `SaveChangesInterceptor` — all `event_*` writes flow through
`IEventService` by design, enforced by the
`Only_EventRepository_Writes_Event_DbSets` architecture test).

### EventService (Scoped, keyed `"event-inner"` — inner of CachingEventService)

Repository: `IEventRepository`.

| Table | R/W |
|-------|-----|
| Events | R/W |
| EventGuideSettings | R/W |
| EventCategories | R/W |
| EventVenues | R/W |
| EventModerationActions | R/W |
| EventPreferences | R/W |
| EventFavourites | R/W |

Cross-section calls limited to `IClock` (plus owning-service lookups for
active-event scoping). Implements `IUserDataContributor`. The inner
service has no `IMemoryCache`.

### CachingEventService (Singleton, `Humans.Events.Services`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, ApprovedEventView>` (`Event.ApprovedEventView`) | Per-Entity | yes | yes | yes (per-slice, inline after delegated write; via `IEventViewInvalidator`) |
| Flat `EventCategoryView` list | Static | yes | yes | yes |
| Flat `EventVenueView` list | Static | yes | yes | yes |
| `EventGuideSettingsView` singleton | Static | yes | yes | yes |

Implements `IEventService` (which extends the cross-section read surface
`IEventServiceRead`), `IEventViewInvalidator`,
`IHostedService` (`StartAsync` warms all four projections).
`IEventServiceRead` (approved events / guide settings / favourite ids) is
registered as a forward to this singleton so cross-section consumers (the
camp detail page's events card, `CampEventsViewComponent`) read from the
cache. The moderator-only `GetAllEventsForDashboardAsync` passes through
to the inner service (needs a fresh pending count; the cache only holds
approved events). Only the event projection is surfaced on
`/Debug/CacheStats`.

---


