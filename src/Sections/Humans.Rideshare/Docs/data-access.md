# Rideshare — Data Access

## Rideshare

Folder: `src/Sections/Humans.Rideshare/Services/` (namespace `Humans.Rideshare.Services`).
**DbContext:** `RideshareDbContext`. `RideshareRepository`
(`src/Sections/Humans.Rideshare/Data/RideshareRepository.cs`) injects
`IDbContextFactory<RideshareDbContext>` directly, one context per call, `AsNoTracking` on reads.
Owns `RideshareTrips`, `RideshareRequests`, `RideshareInterests`, `RideshareSettings`.

The inner `IRideshareService` is wrapped by `Humans.Rideshare.Services.CachingRideshareService`
(Singleton decorator). It owns one `TrackedCache<int, RideshareSnapshot>`
(`Rideshare.Snapshot`), keyed by year. Writes delegate to the inner service, then the whole
cache is cleared (no per-year invalidation tracking — acceptable at this scale).

### RideshareService (Scoped, keyed `"rideshare-inner"` — inner of CachingRideshareService)

Repository: `IRideshareRepository`.

| Table | R/W |
|-------|-----|
| RideshareTrips | R/W |
| RideshareRequests | R/W |
| RideshareInterests | R/W |
| RideshareSettings | R/W |

Cross-section calls: `IUserServiceRead`, `IBurnSettingsService`, `INotificationEmitter`,
`IAuditLogService`, `IClock` (NodaTime), plus the section-internal `IRouteProvider`
(OpenRouteService client). The inner service has no `IMemoryCache`.

### CachingRideshareService (Singleton, `Humans.Rideshare.Services`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<int, RideshareSnapshot>` (`Rideshare.Snapshot`) | Per-Year | yes | yes | yes (full `Clear()` after every delegated write) |

Implements `IRideshareService`, `IUserDataContributor` (delegates to the inner service, then
clears the cache), and exposes `ICacheStats SnapshotCacheStats`. No warmup —
`warmOnStartup: false`; `GetSnapshotAsync(year)` populates lazily on miss.

---
