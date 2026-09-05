# EarlyEntry — Data Access

## Early Entry

Folder: `src/Sections/Humans.EarlyEntry/Services/`. Owns no DB tables —
fan-out orchestrator over per-section `IEarlyEntryProvider`
implementations. The inner `IEarlyEntryService` is wrapped by
`Humans.EarlyEntry.Services.CachingEarlyEntryService`
(Singleton decorator inheriting `TrackedCache<Guid, UserEarlyEntry?>`).

### EarlyEntryService (Scoped, keyed `"early-entry-inner"` — inner of CachingEarlyEntryService)

No repository. Injects `IEnumerable<IEarlyEntryProvider>` — every section
that grants early entry implements this and contributes its rows.
Current providers: Camps (`CachingCampService` — camp-lead grants), Shifts
(`VolunteerTrackingExportService` — confirmed build-shift grants), Teams
(`TeamService` — role-gated team EE grants). Each reads through its own
section; fan-out is sequential (`Docs/EarlyEntry.md` Invariants).
No direct DB access, no cache.

### CachingEarlyEntryService (Singleton, `Humans.EarlyEntry.Services`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, UserEarlyEntry?>` (`EarlyEntry.UserEarlyEntry`, lazy, no warmup) | Per-User (caches negative result) | yes | yes | yes (`IEarlyEntryInvalidator.InvalidateUser` / `InvalidateAll`, fired from Shifts, Camps, and Teams writes) |

Implements `IEarlyEntryService`, `IEarlyEntryInvalidator`. `GetRosterAsync`
always delegates to the inner service; `GetForUserAsync` is cached per user,
negative result included. Resolves the keyed Scoped inner via
`IServiceScopeFactory`. Surfaced on `/Debug/CacheStats`.
