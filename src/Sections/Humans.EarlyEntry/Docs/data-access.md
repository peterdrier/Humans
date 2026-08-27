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
Current providers: Camps (`CampService` — camp-lead grants), Shifts
(`VolunteerTrackingExportService` — confirmed build-shift grants), Teams
(`TeamService` — role-gated team EE grants). Each provider reads via its
own section's DbContext, so no shared context instance forces sequential
access; fan-out is nonetheless kept sequential for consistency with the
other contributor orchestrators (`GdprService`, `ICalFeedService`).
No direct DB access, no cache.

### CachingEarlyEntryService (Singleton, `Humans.EarlyEntry.Services`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, UserEarlyEntry?>` (`EarlyEntry.UserEarlyEntry`, lazy, no warmup) | Per-User (caches negative result) | yes | yes | yes (`IEarlyEntryInvalidator.InvalidateUser` / `InvalidateAll`, fired from Shifts, Camps, and Teams writes) |

Implements `IEarlyEntryService`, `IEarlyEntryInvalidator`. `GetRosterAsync`
always delegates to the inner service (admin roster needs live data);
`GetForUserAsync` is cached per-user (including the no-EE negative result
since most users have no EE). Resolves the keyed Scoped inner via
`IServiceScopeFactory`. Surfaced on `/Debug/CacheStats`.

---


