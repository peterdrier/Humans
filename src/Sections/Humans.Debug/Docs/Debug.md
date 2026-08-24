<!-- freshness:triggers
  src/Sections/Humans.Debug/**
  src/Humans.Web/ViewComponents/AdminNavComposition.cs
-->

# Debug - Section Invariants

Developer/diagnostics section. Admin-only pages exposing operational insight that no domain section owns. Owns no tables.

## Concepts

- The **Debug** section is the developer/diagnostics area: admin-only pages surfacing operational insight (client demographics, request health, logs, cache/db stats, configuration status, and maintenance operations) that belongs to no domain section.
- It is the forward home for "any tool a developer wants." New developer/diagnostic pages live here, not under the legacy `/Admin/*` shell route.
- The section owns no domain data. Most figures it shows come from process-local, in-memory trackers that reset on every restart/redeploy.

## Data Model

This section owns no entities. Displayed telemetry comes from in-memory, process-local singletons (`IClientStatsTracker`, `IHttpStatusTracker`, query/cache statistics). Migration status and Hangfire lock cleanup route through diagnostics services rather than domain repositories.

## Routing

All pages live under `/Debug` on `DebugController`. Pages sit at `/Debug/<Page>` directly, not `/Debug/Admin/*`: the whole section is admin-gated with no user-facing pages, so there is no public-vs-admin split to disambiguate. The `/<Section>/Admin/*` shape in [`../../../../memory/architecture/no-admin-url-section.md`](../../../../memory/architecture/no-admin-url-section.md) exists to separate admin actions from public ones inside a mixed section; Debug is admin-only end to end.

| Route | Method | Auth | Purpose |
|-------|--------|------|---------|
| `/Debug/Logs` | GET | Admin | In-memory warning/error log buffer; optional `minLevel` query param filters to `Warning`, `Error`, or `Fatal` |
| `/Debug/HttpErrors` | GET | Admin | Rolling buffer of the last 1000 error responses (status > 399); per-request detail with timestamp, code, method, URL, IP, user, and classified client label |
| `/Debug/Configuration` | GET | Admin | Auto-discovered configuration status |
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

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Admin | Full access to every Debug page except `/Debug/DbVersion`, which is intentionally anonymous |
| All other roles | None - an authenticated non-Admin gets `302 -> /Account/AccessDenied` on every admin-gated `/Debug/*` page (cookie authentication's `AccessDeniedPath`, app-wide) |

## Invariants

- Every Debug page requires `PolicyNames.AdminOnly` (class-level `[Authorize]` on `DebugController`) except `/Debug/DbVersion`, which is intentionally `[AllowAnonymous]` and returns only migration names and counts.
- Debug owns no domain data; its in-memory telemetry is process-local and resets on restart/redeploy.
- New developer/diagnostics pages are added here (`/Debug/*`), never under the legacy `/Admin/*`.
- Debug pages do not mutate domain state. Explicit maintenance POSTs may mutate operational infrastructure state, such as clearing stale Hangfire locks.

## Negative Access Rules

- A non-Admin user cannot reach admin-gated Debug pages: `302 -> /Account/AccessDenied`, pinned by `DebugPageRenderTests`.
- Debug pages cannot change domain state. Operational writes must stay explicitly named maintenance actions.
- No type in the section may bind `IStringLocalizer<T>` for any `T` but `SharedResource`. Debug carries no resource set of its own - every string is English developer copy - and the one localizer it touches is the shared set read as *data* by `/Debug/Translations`. Enforced by `DebugArchitectureTests`.

## Triggers

The telemetry trackers are fed passively by `ClientStatsMiddleware` (page views; error responses for the `/Debug/HttpErrors` buffer) and a `MeterListener` over the ASP.NET Core hosting meter (status codes). 429s are recorded via the rate limiter's `OnRejected` callback rather than `ClientStatsMiddleware` (the limiter rejects before the middleware runs). The only write trigger is `ClearHangfireLocks`, an explicit maintenance POST.

## Cross-Section Dependencies

Debug consumes in-memory telemetry trackers (`IClientStatsTracker`, `IHttpStatusTracker`, query/cache statistics), the configuration registry, and `IAdminDatabaseDiagnosticsService` for migration status and Hangfire lock cleanup.

## Architecture

**Owning services:** None - controller-only diagnostics.
**Owned tables:** None.
**Status:** (G5) Own project at `src/Sections/Humans.Debug` (nobodies-collective/Humans#866). Previously (B), legacy diagnostics migrated from `/Admin/*` to `/Debug/*` in the 2026-06 route cleanup.

- `DebugController` is `internal sealed` in `Humans.Debug.Controllers`, routed by Shell's `SectionControllerFeatureProvider`; it consumes telemetry trackers, configuration metadata, query/cache counters, and admin database diagnostics, all of them Base singletons registered by their owners.
- `Section.Register` is **empty**, and the class ships anyway: `ISection` is what puts the assembly in the discovered-sections log. `Contracts/` is an empty folder - nothing outside the section names a Debug type. The section's own `SectionAdminNav` (`ISectionAdminNav`) contributes its sidebar entries by controller/action *name*, merged into the Shell nav by `AdminNavComposition` (nobodies-collective/Humans#1077).
- The section references `Humans.Base` despite owning no tables: it names `QueryStatistics` (`Humans.Base.Data`) and `InMemoryLogSink` (`Humans.Base.Logging`; lives here rather than in `Humans.Web` because Shell's `LogApiController` reads it too and a section cannot be referenced from Shell's own code). `TrackingMemoryCache` is no longer named directly — `DebugController` reaches active cache-entry counts through `ICacheStatsProvider.GetActiveEntryCounts()` instead of downcasting to the concrete type.
- `TranslationsGalleryModelBuilder` did *not* move in: it enumerates `SharedResource` and `CultureCatalog`, both `Humans.UI` vocabulary, and `SharedResourceParityTests` asserts translation parity through it. It lives in `Humans.UI/Models`. `FormatGalleryModelBuilder` did move in - its only consumer is `/Debug/FormatGallery`.
- **Decorator decision - no caching decorator.** Owns no data; the trackers are already in-memory singletons.
- **Cross-domain navs:** N/A - owns no entities.
