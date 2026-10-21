<!-- freshness:triggers
  src/Sections/Humans.Debug/**
  src/Humans.Base/ViewComponents/AdminNavComposition.cs
  src/Humans.Web/Middleware/ClientStatsMiddleware.cs
  src/Humans.Web/Program.cs
-->

# Debug - Section Invariants

Developer/diagnostics section: admin-only pages exposing operational insight that no domain section owns, plus the design reference (colour palette, widget gallery, format and translation galleries). Owns no tables.

## Concepts

- The **Debug** section is the developer/diagnostics area: admin-only pages surfacing operational insight (client demographics, request health, logs, cache/db stats, timings, configuration status, the section catalog, and maintenance operations) that belongs to no domain section.
- It is the home for "any tool a developer wants." New developer/diagnostic pages live at `/Debug/*`, not under the `/Admin/*` shell route.
- It also hosts the **design reference**: `/ColorPalette` (tokens, controls, typography), `/Debug/WidgetGallery` (every reusable widget rendered against sample data), `/Debug/FormatGallery` and `/Debug/Translations`.
- The section owns no domain data. Most figures it shows come from process-local, in-memory trackers that reset on every restart/redeploy.
- It is the permanent home of **data migrations**: every section's operator-run copy from a legacy shape into its own (see [Data migrations](#data-migrations)).

## Data Model

This section owns no entities. Displayed telemetry comes from in-memory, process-local singletons (`IClientStatsTracker`, `IHttpStatusTracker`, query/cache statistics). Migration status and Hangfire lock cleanup route through diagnostics services rather than domain repositories.

## Routing

One controller per audience: `DebugController` (`/Debug/*`, diagnostics and the reflection galleries), `WidgetGalleryController` (`/Debug/WidgetGallery`) and `ColorPaletteController` (`/ColorPalette`). Pages sit at `/Debug/<Page>` directly, not `/Debug/Admin/*`: the section has no user-facing pages, so there is no public-vs-admin split to disambiguate. The `/<Section>/Admin/*` shape in [`../../../../memory/architecture/no-admin-url-section.md`](../../../../memory/architecture/no-admin-url-section.md) exists to separate admin actions from public ones inside a mixed section.

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
| `/Debug/Users` | GET | Admin | All-users debug grid (`UsersDebugController`): flat paginated/sortable table of every user incl. tombstones, every column from the cached `UserInfo` snapshot via `IUserService.GetAllRawUserInfosAsync`; page size clamped 10-200, offsets past the end give an empty page |
| `/Debug/DataMigrations` | GET | Admin | Every registered data migration: section, key, title, what it retires, pending count and every pending row |
| `/Debug/DataMigrations/Apply` | POST | Admin | Run one migration (`key`) for the acting admin; `data-confirm`, antiforgery, audited `DataMigrationApplied` |
| `/api/data-migrations` | GET | Anonymous | Data-migration progress JSON — keys, pending counts, done flags; never row data |
| `/Debug/Timings` | GET | Admin | Operation timing table: per-operation call count, last/avg/min/max ms, total ms, last-called timestamp; ordered by total cost descending |
| `/Debug/Sections` | GET | Admin | The DI-published section catalog: every discovered section with its tables, contracts, resx set, seams, dependencies, and which of guide page / agent doc key / issue queue it has |
| `/Debug/WidgetGallery` | GET | Admin | Catalog of every reusable widget rendered against sample data and live keys |
| `/ColorPalette` | GET | Anonymous | Static design reference: colour tokens, controls, typography |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Admin | Full access to every page |
| Anyone | `/Debug/DbVersion` (migration names and counts), `/api/data-migrations` (data-migration keys and counts) and `/ColorPalette` (static markup, no data) |
| All other roles | Nothing else - an authenticated non-Admin gets `302 -> /Account/AccessDenied` on every admin-gated page (cookie authentication's `AccessDeniedPath`, app-wide) |

## Invariants

- Shared table currency and number cells use the selected UI culture; numeric sort values stay invariant.

- Every page requires `PolicyNames.AdminOnly` (class-level `[Authorize]` on every controller in the section) except the deliberate anonymous surfaces: `/Debug/DbVersion`, which returns only migration names and counts, `/api/data-migrations`, which returns only data-migration keys, counts and done flags, and `/ColorPalette`, which renders static markup. Pinned by `DebugArchitectureTests`, which discovers the controllers from the assembly rather than listing them, and by `DataMigrationsTests` for the minimal-API endpoint.
- Sensitive configuration values never render in full on `/Debug/Configuration`: at most the first four characters, and values of four characters or fewer are fully masked. Pinned by `DebugControllerTests`.
- Debug owns no domain data; its in-memory telemetry is process-local and resets on restart/redeploy.
- New developer/diagnostics pages are added here (`/Debug/*`), never under `/Admin/*`.
- Debug pages do not mutate domain state themselves. Explicit maintenance POSTs may mutate operational infrastructure state, such as clearing stale Hangfire locks; that clear is logged at Warning so it reaches production logs. The one domain write reachable from here is `/Debug/DataMigrations/Apply`, and it runs the *owning* section's delegate — Debug never touches another section's data itself.
- Buffer reads (`/Debug/Logs`, `/Debug/HttpErrors`) clamp `count` to 1..1000.

## Negative Access Rules

- A non-Admin user cannot reach admin-gated Debug pages: `302 -> /Account/AccessDenied`, pinned by `DebugPageRenderTests` (local-only, `Humans.Integration.Tests`).
- Debug pages cannot change domain state except through a data migration's own Apply, which is admin-only, antiforgery-protected, and audited. Operational writes must stay explicitly named maintenance actions.
- `/api/data-migrations` never returns row data — no user id, email or legacy value. Pinned by `DataMigrationsTests`.
- No type in the section may bind `IStringLocalizer<T>` for any `T` but `SharedResource`. Debug carries no resource set of its own - every string is English developer copy - and the one localizer it touches is the shared set read as *data* by `/Debug/Translations`. Enforced by `DebugArchitectureTests`.

## Triggers

The telemetry trackers are fed passively by `ClientStatsMiddleware` (page views; error responses for the `/Debug/HttpErrors` buffer) and a `MeterListener` over the ASP.NET Core hosting meter (status codes). 429s are recorded via the rate limiter's `OnRejected` callback in `Program.cs` rather than `ClientStatsMiddleware` (the limiter rejects before the middleware runs). The write triggers are `ClearHangfireLocks`, an explicit maintenance POST, and `/Debug/DataMigrations/Apply`, which runs one section's migration.

## Data migrations

The single screen and status endpoint for every section's legacy → new copy, as `memory/process/no-data-backfills.md` requires (review every row → confirm → audit), so no section ever ships its own Temp backfill screen.

- **Seam:** a section implements `ISectionDataMigrations` (`Humans.Base.Interfaces`) at its project root (`SectionDataMigrations`) and returns `DataMigration` records: a stable section-first `Key` (`users.stub-profiles`), `Section`, `Title`, `Retires` (what the retirement PR deletes), a `Status` delegate returning `DataMigrationStatus` (column headings plus **every** pending row — no sampling, no paging) and an `Apply` delegate taking the actor and returning rows written. The delegates resolve the owning section's own services from the request scope, so the copy stays inside the owner; Debug only calls the interface. Discovered like every other contribution seam.
- **Host:** `DataMigrationService` (scoped, the one registration in `Section.Register`) lists the migrations ordered by section then key, reads their status, and applies one by key — then writes `AuditAction.DataMigrationApplied` (entity type `DataMigration`, description naming key, section and rows written, the acting admin as actor). An unknown key writes and audits nothing (404 on the screen).
- **Screen:** `/Debug/DataMigrations` (`DataMigrationsController`, `AdminOnly`, linked from the Debug admin group) shows each migration with its pending rows and an **Apply (N)** button (`data-confirm`, `[ValidateAntiForgeryToken]`). Every migration is idempotent: a second apply finds nothing pending, and a done migration shows a Done badge instead of the button.
- **Endpoint:** `GET /api/data-migrations` (`SectionEndpoints : ISectionEndpoints`, `AllowAnonymous`) returns `{ "version": "...", "migrations": [ { "key": "...", "pending": 0, "done": true } ] }` — the `version` is `/api/version`'s. A development session polls production's endpoint to learn when a migration is done; that reading is the approval for its retirement and drop PRs (`memory/architecture/no-drops-until-prod-verified.md`).
- **Why the counts are safe to publish:** they are membership-shaped aggregates (how many profiles still hold a legacy value) with no row data — no id, name, address or value — and say nothing about any one member. Row data stays behind `AdminOnly` on the screen.
- **Lifecycle:** a move commit adds its migration; QA and production operators apply it from the screen; once production reports `done`, a retirement PR deletes the migration (and its fallback and obsolete fields), then a drop PR removes the legacy columns. Today's registrations: Users' four (`users.stub-profiles`, `users.names-onto-user`, `users.profile-pictures-to-filesystem`, `users.legacy-identity-emails`) — see `Users.md` — plus one per section the Users decomposition moved data into (Consent, Email, GoogleIntegration, Tickets).

## Cross-Section Dependencies

Debug consumes in-memory telemetry trackers (`IClientStatsTracker`, `IHttpStatusTracker`, query/cache statistics), the configuration registry, `IAdminDatabaseDiagnosticsService` for migration status and Hangfire lock cleanup, and `ISectionCatalog` (`Humans.Base.Interfaces`, published by Shell at startup) to render `/Debug/Sections`. That page names no section: everything on it arrives through the catalog.

`/Debug/DataMigrations` and `/api/data-migrations` compose every section's `ISectionDataMigrations` contribution and audit through `IAuditLogService`; they name no section.

The all-users grid reads `IUserService.GetAllRawUserInfosAsync` and the `UserInfo` projection, plus Consent's `IConsentService.GetConsentReviewsAsync` for its consent column and Tickets' `ITicketService.GetAllParticipationsAsync` for its has-a-ticket column (any year).

The widget gallery and the dashboard card read other sections through their contracts: `ITeamService`, `ICampService`, `IShiftManagementService`, `IEventService`, `IUserService`, `ITicketService` (the membership card's ticket bit), `ICommunicationPreferenceService` (Email; its marketing bit and the users debug page's opt-out column), `IShiftView`, and `ISettingsService` (Settings' supplier of the active event). The gallery also references the section assemblies whose public view components it renders as `<vc:>` tag helpers (Camps, Users, Tickets, Shifts, Calendar, AuditLog, Teams, Events); that fan-in is the page's job and is opened in `Views/_ViewImports.cshtml`. `MemberTermStatus` (Governance), `MyGoogleResources` (GoogleIntegration) and `NotificationBell` (Notifications) get no gallery card at all — they already render on the page through the member-dashboard and header-right chrome-slot demos, and a `WidgetGallery` contribution would only duplicate them.

## Architecture

**Owning services:** `DataMigrationService` (scoped; hosts the data migrations, owns no data). The diagnostics controllers project singleton snapshots into view models. One static calculator (`UserSetMembershipCalculator`) backs the dashboard card.
**Owned tables:** None.

- All controllers are `internal sealed` in `Humans.Debug.Controllers`, routed by Shell's `SectionControllerFeatureProvider`. `DebugController` consumes telemetry trackers, configuration metadata, query/cache counters, and admin database diagnostics, all of them Base singletons registered by their owners.
- `Section.Register` adds only `DataMigrationService`; `ISection` is also what puts the assembly in the discovered-sections log. `Contracts/` holds only a README - nothing outside the section names a Debug type (the data-migration seam lives in Base).
- `SectionEndpoints` (`ISectionEndpoints`) maps `/api/data-migrations` beside the Shell's `/api/version`.
- Root-level seams contribute by name: `SectionAdminNav` (`ISectionAdminNav`) supplies the Debug sidebar group (diagnostics plus the design references), merged into the Shell nav by `AdminNavComposition`; `SectionChrome` (`ISectionChrome`) contributes the `UserSetMembershipCard` view component to the admin dashboard's chrome slot.
- `/Debug/WidgetGallery` itself is a chrome-slot *consumer*: Base-hosted widgets render through `<vc:chrome-slot>`, `<vc:admin-breadcrumb>`, `<vc:section-nav>`, `<vc:things-to-do>`, and Users' and Shifts' own gallery samples render through one `<vc:chrome-slot name="@ChromeSlots.WidgetGallery">` call, fed by their `ISectionChrome` contributions (`UsersGalleryViewComponent`, `ShiftsGalleryViewComponent`) — the page holds no per-section state for those cards and needs no reference to the sections that contribute nothing else it uses.
- The section references `Humans.Base` despite owning no tables: it names `QueryStatistics` (`Humans.Base.Data`) and the host-local `InMemoryLogSink` (`Humans.Base.Logging`; Shell configures it from `Program.cs`, and Backdoor's `BackdoorLogsController` reads the same DI instance at `/api/backdoor/logs`). Cache-entry counts come from `ICacheStatsProvider.GetActiveEntryCounts()`; Debug never names `TrackingMemoryCache`.
- `TranslationsGalleryModelBuilder` lives in Base (`src/Humans.Base/Models/TranslationsGalleryViewModel.cs`): it enumerates `SharedResource` and `CultureCatalog`, and `SharedResourceParityTests` asserts translation parity through it. `FormatGalleryModelBuilder` lives here - its only consumer is `/Debug/FormatGallery`. Its language comparison scopes both formatting and UI cultures for each example, restoring the caller’s cultures afterward.
- **Decorator decision - no caching decorator.** Owns no data; the trackers are already in-memory singletons.
- **Cross-domain navs:** N/A - owns no entities.
