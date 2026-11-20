# Rideshare — Data Access

## Rideshare

Folder: `src/Sections/Humans.Rideshare/Services/` (namespace `Humans.Rideshare.Services`).
**DbContext:** `RideshareDbContext`. `RideshareRepository`
(`src/Sections/Humans.Rideshare/Data/RideshareRepository.cs`) injects
`IDbContextFactory<RideshareDbContext>` directly, one context per call, `AsNoTracking` on reads.
Owns `rideshare_trips`, `rideshare_requests`, `rideshare_interests`, `rideshare_settings`.

The inner `IRideshareService` is wrapped by `Humans.Rideshare.Services.CachingRideshareService`
(Singleton decorator). It owns one `TrackedCache<Guid, RideshareSnapshot>`
(`Rideshare.Snapshot`), keyed by event id. Writes delegate to the inner service, then the whole
cache is cleared (no per-event invalidation tracking — acceptable at this scale).

### RideshareService (Scoped, keyed `"rideshare-inner"` — inner of CachingRideshareService)

Repository: `IRideshareRepository`.

| Table | R/W |
|-------|-----|
| rideshare_trips | R/W |
| rideshare_requests | R/W |
| rideshare_interests | R/W |
| rideshare_settings | R/W |

Cross-section calls: `IUserService`, `ISettingsService`, `INotificationEmitter`,
`IAuditLogService`, `IClock` (NodaTime), plus the section-internal `IRouteProvider`
(OpenRouteService client). The inner service has no `IMemoryCache`.

### CachingRideshareService (Singleton, `Humans.Rideshare.Services`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, RideshareSnapshot>` (`Rideshare.Snapshot`) | Per-Event | yes | yes | yes (full `Clear()` after every delegated write) |

Implements `IRideshareService`, `IUserDataContributor` and `IUserMerge` (both delegate to the
inner service, then clear the cache), and exposes `ICacheStats SnapshotCacheStats`. No warmup —
`warmOnStartup: false`; `GetSnapshotAsync(eventSettingsId)` populates lazily on miss.

---
