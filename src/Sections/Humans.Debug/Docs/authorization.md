# Debug — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `DebugController` | Class | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `DebugController` actions — `Logs`, `HttpErrors`, `Maintenance`, `Configuration`, `DbStats`, `CacheStats`, `ClientStats`, `Timings`, `Sections`, `FormatGallery`, `Translations` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` (all inherit the class-level policy; read-only) |
| `DebugController.ClearHangfireLocks` / `ResetDbStats` / `ResetCacheStats` | Action | `Admin` | `PolicyNames.AdminOnly` (the mutating POSTs) |
| `DebugController.DbVersion` | Action | `AllowAnonymous` | Override |
| `UsersDebugController` (`Index`) | Class | `Board, Admin` | `PolicyNames.BoardOrAdmin` (`/Debug/Users`) |
| `DataMigrationsController` (`Index`) | Class | `Board, Admin` | `PolicyNames.BoardOrAdmin` (`/Debug/DataMigrations`) |
| `DataMigrationsController.Apply` | Action | `Admin` | `PolicyNames.AdminOnly` (`[ValidateAntiForgeryToken]`) |
| `GET /api/data-migrations` (minimal API, `ISectionEndpoints`) | Endpoint | `AllowAnonymous` | Keys, pending counts and done flags only — never row data |
| `ColorPaletteController` | Class | `AllowAnonymous` | — (design reference page: palette, controls, typography; linked from the admin sidebar "Debug" group) |
| `WidgetGalleryController` | Class | `Board`, `Admin` | `PolicyNames.BoardOrAdmin` (Board-or-Admin catalog of reusable UI widgets; companion to `/ColorPalette`) |
