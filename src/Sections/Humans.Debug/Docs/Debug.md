<!-- freshness:triggers
  src/Sections/Humans.Debug/**
  src/Humans.Web/ViewComponents/AdminNavComposition.cs
  src/Humans.Web/Middleware/ClientStatsMiddleware.cs
  src/Humans.Web/Program.cs
-->

# Debug - Section Invariants

Developer/diagnostics section: admin-only pages exposing operational insight that no domain section owns, plus the design reference (colour palette, widget gallery, format and translation galleries). Owns no tables.

## Concepts

- The **Debug** section is the developer/diagnostics area: admin-only pages surfacing operational insight (client demographics, request health, logs, cache/db stats, timings, configuration status, the section catalog, and maintenance operations) that belongs to no domain section.
- It is the home for "any tool a developer wants." New developer/diagnostic pages live at `/Debug/*`, not under the `/Admin/*` shell route.
- It also hosts the **design reference**: `/ColorPalette` (tokens, controls, typography), `/WidgetGallery` (every reusable widget rendered against sample data), `/Debug/FormatGallery` and `/Debug/Translations`.
- The section owns no domain data. Most figures it shows come from process-local, in-memory trackers that reset on every restart/redeploy.

## Data Model

This section owns no entities. Displayed telemetry comes from in-memory, process-local singletons (`IClientStatsTracker`, `IHttpStatusTracker`, query/cache statistics). Migration status and Hangfire lock cleanup route through diagnostics services rather than domain repositories.

## Routing

Three controllers, one per audience: `DebugController` (`/Debug/*`, diagnostics and the two reflection galleries), `WidgetGalleryController` (`/WidgetGallery`) and `ColorPaletteController` (`/ColorPalette`). Pages sit at `/Debug/<Page>` directly, not `/Debug/Admin/*`: the section has no user-facing pages, so there is no public-vs-admin split to disambiguate. The `/<Section>/Admin/*` shape in [`../../../../memory/architecture/no-admin-url-section.md`](../../../../memory/architecture/no-admin-url-section.md) exists to separate admin actions from public ones inside a mixed section.

| Route | Method | Auth | Purpose |
|-------|--------|------|---------|
| `/Debug/Logs` | GET | Admin | In-memory warning/error log buffer; optional `minLevel` query param filters to `Warning`, `Error`, or `Fatal` |
| `/Debug/HttpErrors` | GET | Admin | Rolling buffer of the last 1000 error responses (status > 399); per-request detail with timestamp, code, method, URL, IP, user, and classified client label |
| `/Debug/Configuration` | GET | Admin | Auto-discovered configuration status; sensitive values masked |
| `/Debug/DbVersion` | GET | Anonymous | Migration status JSON for deployment tooling |
| `/Debug/DbStats` | GET | Admin | Query statistics |
| `/Debug/DbStats/Reset` | POST | Admin | Reset query statistics |
| `/Debug/CacheStats` | GET | Admin | Cache hit/miss/size statistics |
| `/Debug/CacheStats/Reset` | POST | Admin | Reset cache statistics |
| `/Debug/ClientStats` | GET | Admin | Browser/device/status-code telemetry |
| `/Debug/FormatGallery` | GET | Admin | Date/time formatting reference |
| `/Debug/Translations` | GET | Admin | Localisation string reference gallery |
| `/Debug/Maintenance` | GET | Admin | Maintenance operations |
| `/Debug/Maintenance/ClearHangfireLocks` | POST | Admin | Clear stale Hangfire locks |
| `/Debug/Timings` | GET | Admin | Operation timing table: per-operation call count, last/avg/min/max ms, total ms, last-called timestamp; ordered by total cost descending |
| `/Debug/Sections` | GET | Admin | The DI-published section catalog: every discovered section with its tables, contracts, resx set, seams, dependencies, and which of guide page / agent doc key / issue queue it has |
| `/WidgetGallery` | GET | Admin | Catalog of every reusable widget rendered against sample data and live keys |
| `/ColorPalette` | GET | Anonymous | Static design reference: colour tokens, controls, typography |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Admin | Full access to every page |
| Anyone | `/Debug/DbVersion` (migration names and counts) and `/ColorPalette` (static markup, no data) |
| All other roles | Nothing else - an authenticated non-Admin gets `302 -> /Account/AccessDenied` on every admin-gated page (cookie authentication's `AccessDeniedPath`, app-wide) |

## Invariants

- Every page requires `PolicyNames.AdminOnly` (class-level `[Authorize]` on `DebugController` and `WidgetGalleryController`) except two deliberate anonymous surfaces: `/Debug/DbVersion`, which returns only migration names and counts, and `/ColorPalette`, which renders static markup. Pinned by `DebugArchitectureTests`.
- Sensitive configuration values never render in full on `/Debug/Configuration`: at most the first four characters, and values of four characters or fewer are fully masked. Pinned by `DebugControllerTests`.
- Debug owns no domain data; its in-memory telemetry is process-local and resets on restart/redeploy.
- New developer/diagnostics pages are added here (`/Debug/*`), never under `/Admin/*`.
- Debug pages do not mutate domain state. Explicit maintenance POSTs may mutate operational infrastructure state, such as clearing stale Hangfire locks; that clear is logged at Warning so it reaches production logs.
- Buffer reads (`/Debug/Logs`, `/Debug/HttpErrors`) clamp `count` to 1..1000.

## Negative Access Rules

- A non-Admin user cannot reach admin-gated Debug pages: `302 -> /Account/AccessDenied`, pinned by `DebugPageRenderTests` (local-only, `Humans.Integration.Tests`).
- Debug pages cannot change domain state. Operational writes must stay explicitly named maintenance actions.
- No type in the section may bind `IStringLocalizer<T>` for any `T` but `SharedResource`. Debug carries no resource set of its own - every string is English developer copy - and the one localizer it touches is the shared set read as *data* by `/Debug/Translations`. Enforced by `DebugArchitectureTests`.

## Triggers

The telemetry trackers are fed passively by `ClientStatsMiddleware` (page views; error responses for the `/Debug/HttpErrors` buffer) and a `MeterListener` over the ASP.NET Core hosting meter (status codes). 429s are recorded via the rate limiter's `OnRejected` callback in `Program.cs` rather than `ClientStatsMiddleware` (the limiter rejects before the middleware runs). The only write trigger is `ClearHangfireLocks`, an explicit maintenance POST.

## Cross-Section Dependencies

Debug consumes in-memory telemetry trackers (`IClientStatsTracker`, `IHttpStatusTracker`, query/cache statistics), the configuration registry, `IAdminDatabaseDiagnosticsService` for migration status and Hangfire lock cleanup, and `ISectionCatalog` (`Humans.Base.Interfaces`, published by Shell at startup) to render `/Debug/Sections`. That page names no section: everything on it arrives through the catalog.

The widget gallery and the dashboard card read other sections through their contracts: `ITeamServiceRead`, `ICampServiceRead`, `IShiftManagementServiceRead`, `IEventServiceRead`, `IUserServiceRead`, `IShiftView`, and `IBurnSettingsService` (the full Shifts interface, for one read - no read-split exists yet). The gallery also references the section assemblies whose public view components it renders as `<vc:>` tag helpers (Camps, Users, Tickets, Shifts, Calendar, AuditLog, Teams, Events); that fan-in is the page's job and is opened in `Views/_ViewImports.cshtml`.

## Architecture

**Owning services:** None - controllers project singleton snapshots into view models. One static calculator (`UserSetMembershipCalculator`) backs the dashboard card.
**Owned tables:** None.

- All three controllers are `internal sealed` in `Humans.Debug.Controllers`, routed by Shell's `SectionControllerFeatureProvider`. `DebugController` consumes telemetry trackers, configuration metadata, query/cache counters, and admin database diagnostics, all of them Base singletons registered by their owners.
- `Section.Register` is **empty**, and the class ships anyway: `ISection` is what puts the assembly in the discovered-sections log. `Contracts/` holds only a README - nothing outside the section names a Debug type.
- Two root-level seams contribute by name: `SectionAdminNav` (`ISectionAdminNav`) supplies the Diagnostics and Design sidebar groups, merged into the Shell nav by `AdminNavComposition`; `SectionChrome` (`ISectionChrome`) contributes the `UserSetMembershipCard` view component to the admin dashboard's chrome slot.
- The section references `Humans.Base` despite owning no tables: it names `QueryStatistics` (`Humans.Base.Data`) and the host-local `InMemoryLogSink` (`Humans.Base.Logging`; Shell configures it from `Program.cs`, and Backdoor's `BackdoorLogsController` reads the same DI instance at `/api/backdoor/logs`). Cache-entry counts come from `ICacheStatsProvider.GetActiveEntryCounts()`; Debug never names `TrackingMemoryCache`.
- `TranslationsGalleryModelBuilder` lives in Base (`src/Humans.Base/Models/TranslationsGalleryViewModel.cs`): it enumerates `SharedResource` and `CultureCatalog`, and `SharedResourceParityTests` asserts translation parity through it. `FormatGalleryModelBuilder` lives here - its only consumer is `/Debug/FormatGallery`.
- **Decorator decision - no caching decorator.** Owns no data; the trackers are already in-memory singletons.
- **Cross-domain navs:** N/A - owns no entities.
