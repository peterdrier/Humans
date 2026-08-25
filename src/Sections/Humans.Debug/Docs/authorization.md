# Debug — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `DebugController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `DebugController` actions — `Logs`, `HttpErrors`, `Maintenance`, `ClearHangfireLocks`, `Configuration`, `DbStats`/`ResetDbStats`, `CacheStats`/`ResetCacheStats`, `ClientStats`, `Timings`, `FormatGallery`, `Translations` | Action | `Admin` | `PolicyNames.AdminOnly` (all inherit the class-level policy) |
| `DebugController.DbVersion` | Action | `AllowAnonymous` | Override |
| `ColorPaletteController` | Class | `AllowAnonymous` | — (design reference page: palette, controls, typography; linked from the admin sidebar "Design" group) |
| `WidgetGalleryController` | Class | `Admin` | `PolicyNames.AdminOnly` (admin-only catalog of reusable UI widgets; companion to `/ColorPalette`) |
