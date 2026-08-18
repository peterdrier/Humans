# Debug — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `DebugController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `DebugController` actions — `Logs`, `HttpErrors`, `Maintenance`, `ClearHangfireLocks`, `Configuration`, `DbStats`/`ResetDbStats`, `CacheStats`/`ResetCacheStats`, `ClientStats`, `Timings`, `FormatGallery`, `Translations` | Action | `Admin` | `PolicyNames.AdminOnly` (all inherit the class-level policy) |
| `DebugController.DbVersion` | Action | `AllowAnonymous` | Override |
