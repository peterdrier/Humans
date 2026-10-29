# Debug — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `DebugController` | Class | `Admin` | `PolicyNames.AdminOnly` |
| `DebugController` actions — `Logs`, `HttpErrors`, `Maintenance`, `ClearHangfireLocks`, `Configuration`, `DbStats`/`ResetDbStats`, `CacheStats`/`ResetCacheStats`, `ClientStats`, `Timings`, `Sections`, `FormatGallery`, `Translations` | Action | `Admin` | `PolicyNames.AdminOnly` (all inherit the class-level policy) |
| `DebugController.DbVersion` | Action | `AllowAnonymous` | Override |
| `UsersDebugController` (`Index`) | Class | `Admin` | `PolicyNames.AdminOnly` (`/Debug/Users`) |
| `DataMigrationsController` (`Index`, `Apply`) | Class | `Admin` | `PolicyNames.AdminOnly` (`/Debug/DataMigrations`; `Apply` is `[ValidateAntiForgeryToken]`) |
| `GET /api/data-migrations` (minimal API, `ISectionEndpoints`) | Endpoint | `AllowAnonymous` | Keys, pending counts and done flags only — never row data |
| `ColorPaletteController` | Class | `AllowAnonymous` | — (design reference page: palette, controls, typography; linked from the admin sidebar "Debug" group) |
| `WidgetGalleryController` | Class | `Board`, `Admin` | `PolicyNames.BoardOrAdmin` (Board-or-Admin catalog of reusable UI widgets; companion to `/ColorPalette`) |
