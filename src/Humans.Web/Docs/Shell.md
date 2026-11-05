<!-- freshness:triggers
  src/Humans.Web/**
  src/Humans.Base/Interfaces/ISection*.cs
  src/Humans.Base/Interfaces/IUserPart.cs
  src/Humans.Base/ViewComponents/**
-->
<!-- freshness:flag-on-change
  What the Shell holds and how it composes section contributions. Flag when a seam, a composer, a hosting provider or a platform service is added, moved or removed.
-->

# Humans.Web — the Shell

`src/Humans.Web` is the host: chrome, page composition, platform context. Nothing references it. The rules it obeys live in [`design-rules.md`](../../../docs/architecture/design-rules.md) (*Section contributions to the Shell*); this doc says what the Shell holds and how each seam is composed.

## What it holds

- **Composition root.** `Program.cs` builds the app from the discovered sections: `AddHumansPersistence` (the per-section contexts and the migration runner), `AddHumansIdentity`, `AddHumansAuthorizationPolicies`, `AddHumansInfrastructure`, section discovery and contribution registration (`Extensions/SectionDiscoveryExtensions.cs`), `SectionCatalogBuilder` → `ISectionCatalog`, `SectionActivation`, recurring jobs (`RecurringJobExtensions.UseHumansRecurringJobs`). The two remaining by-name section wirings are `Extensions/Sections/AdminSectionExtensions.cs` and `AuthSectionExtensions.cs`. The vendor-SDK bootstraps that are not a section's own live under `Extensions/Infrastructure/`.
- **Hosting** (`Hosting/`): `SectionControllerFeatureProvider` and `SectionViewComponentFeatureProvider` (route internal section controllers and components), `DevLoginControllerExclusionProvider`, `GatedUploadsFileProvider`, `TempDataCookieValidationMiddleware`.
- **Chrome:** `_Layout`, `_AdminLayout`, `_LoginPartial`, `_LanguageChooser`, `_AuthorizationPill`, `_VersionInfo` under `Views/Shared/`; the admin composers `AdminSidebar`, `AdminTabs`, `AdminSummary` and the `LoginMethodCard` under `ViewComponents/`; `ShellChrome` is the Shell's own `ISectionChrome` contribution, registered by name because discovery walks section assemblies only.
- **Shell frames, not sections:** `/` (Home), `/About`, `/Account`, `/Admin/*` and the language and timezone endpoints (`Controllers/`). `/Admin/*` is a nav holder for admin-only actions whose services belong to the sections they act on; new admin pages live at `/<Section>/Admin/*` ([`no-admin-url-section`](../../../memory/architecture/no-admin-url-section.md)).
- **Platform context:** `SystemDbContext` + its factory (`Data/`), holding the framework-owned tables no section could plausibly own (today `DataProtectionKeys`); its migrations under `Migrations/System/`. Adding a table to it is Peter's call.
- **Platform services** (`Services/`): `FileSystemFileStorage` (the one `IFileStorage`), `HumansMetricsService` (reads its gauges through section public contracts, resolved per scrape), client/HTTP/user-activity stats trackers, `TrackingMemoryCache`, the login throttles and previews, `AdminDatabaseDiagnosticsService` with its repository under `Repositories/Admin/`.
- **Cross-cutting request plumbing:** `Middleware/` (CSP nonce, client stats, private uploads, user activity, view-component tag survival), `ExceptionHandlers/`, `Filters/AuthorizationPillFilter`, `Localization/UiCultureOnlyRequestCultureProvider`, `ModelBinders/`, `Health/ConfigurationHealthCheck`, `Authorization/` (policy registration, claims transformation, the membership and name-required filters, `HttpCurrentUserContext`). No resource authorization handler lives here.

## Seam catalog

A section contributes to the Shell by implementing a seam on its `Section` class. The seams are the `ISectionContribution`-derived interfaces in `Humans.Base/Interfaces/`; discovery registers every implementation as a singleton against each seam it implements. The composers below are where each contribution is rendered or applied.

| Seam | Contributes | Composed by |
|---|---|---|
| `ISectionNav` | Member top-nav links (and dropdown `Children`) | `SectionNavViewComponent` (base) |
| `ISectionAdminNav` | Admin sidebar groups and items | `AdminNavComposition.Compose` (base) — merges contributions into shared groups by `AdminNavGroup.Label`; rendered by `AdminSidebarViewComponent`, `AdminTabsViewComponent` (Shell) and `AdminBreadcrumbViewComponent` (base) |
| `ISectionAdminTiles` | Admin dashboard summary tiles | `AdminSummaryViewComponent` (Shell); a `null` tile value renders nothing, not a zero |
| `ISectionChrome` | Layout-embedded view components | `ChromeSlotViewComponent` (base), named slots in `ChromeSlots` |
| `ISectionMemberDashboard` | Member-dashboard view components | `ChromeSlotViewComponent`, same slots |
| `ISectionThingsToDo` | Member things-to-do entries | `ThingsToDoViewComponent` (base); returning nothing is the normal case |
| `IUserPart` | View components on another section's user-facing page, into slots named in `UserPartSlots` | `UserPartsViewComponent` (base) |
| `ISectionJobs` | Hangfire recurring-job descriptors (id, type, cron) | `RecurringJobExtensions.UseHumansRecurringJobs` — merges into the roll-call and drives the stale-job sweep; unexpected registration or sweep failures log Error, Hangfire distributed-lock timeouts log Warning without stacks |
| `ISectionHealthChecks` | Health checks | builder-time, against `IHealthChecksBuilder` in `Program.cs` |
| `ISectionConfiguration` | The appsettings keys and environment variables the section's code reads | builder-time, into `ConfigurationRegistry` via `AddConfigurationMetadata` — the inventory behind the admin Configuration page |
| `ISectionEndpoints` | Endpoint mapping (SignalR hubs, hubs routing cannot discover) | endpoint-mapping time, against `IEndpointRouteBuilder` in `Program.cs` — necessarily later than `ISection.Register` |
| `ISectionPolicies` | Authorization policy registration | `AuthorizationPolicyExtensions` → `Configure<AuthorizationOptions>` (additive; a section's own policy names live on its `<Section>Policies` contracts class, `PolicyNames` keeps the cross-section ones). Every `PolicyNames` constant must resolve to a registered policy |
| `ISectionRoles` | Grantable roles (`RoleDefinition`) | Auth's `RoleCatalog` singleton — role assignment, the Staff page, Guide headings, dev personas and the `AnyAdminRole` policy |
| `ISectionAnnotations` | Per-section facts for the published catalog (guide page, agent doc key, issue queue) | `SectionCatalogBuilder` → `ISectionCatalog`, rendered at `/Debug/Sections`; unmatched annotations are logged at startup and shown there |
| `ISectionSettings` (Settings' `Contracts/`) | `/Settings` tabs (key, label, view component, policy) | Settings' own `SettingsTabComposition` — filters each tab by its `Policy`; rendered by `SettingsTabsViewComponent` |
| `ISectionAccessMatrix` | Help-widget access-matrix rows | `AccessMatrixViewComponent` (base) and the agent's preloaded corpus; entries carry an explicit `Order` |
| `ISectionHelp` | Guide/Glossary help content (Markdown in the section's `Docs/help/`, embedded) | `AccessMatrixViewComponent`'s modal and the agent's preloaded glossary corpus; entries carry an explicit `Order` |
| `ISectionDataMigrations` | Operator-run legacy → new data copies | Debug's `DataMigrationService` — `/Debug/DataMigrations` and the anonymous counts-only `/api/data-migrations` |

Section-owned seams that are not Shell composition but follow the same discovery (`ICampPart` on Camps' `.Contracts` leaf, `IIssueQueueOwner` in Issues' `Contracts/`) are documented by their owning sections.

## Composition mechanics

- **Ordering.** The descriptor seams (`ISectionNav`, `ISectionAdminNav` items, `ISectionAdminTiles`, `ISectionChrome`, `ISectionMemberDashboard`, `ISectionThingsToDo`, `ISectionSettings`) carry a `Weight` and sort by it alone, stably, nested lists included; `ISectionHelp` and `ISectionAccessMatrix` order by their entries' explicit `Order`. Admin sidebar groups are the exception: they carry no weight and sort alphabetically by label. The member nav reproduces an editorial order accumulated over time; its weights are not to be re-derived.
- **Policy on descriptors.** `ISectionSettings` is the descriptor seam whose entries carry a policy. The composer filters: a tab whose policy the user fails is absent from the strip, a tab declaring no policy is offered to every authenticated user (and decides itself whether it renders editable or read-only), and `/Settings` never 404s — a user who passes no policy gets the page with an empty state.
- **View component slots.** `ChromeComponent`, `SettingsTab` and `UserPart` carry the component as a `Type`; the host names the slot (`ChromeSlots.HeaderRight`, `UserPartSlots.BoardVoteApplicant`) and declares its `TArgs` with `[ViewComponentSlot]`. `ViewComponentSlotContractTests` checks every contributed type against its slot; `ViewComponentTagSurvivalMiddleware` is the dev/QA response scanner that warns when an unresolved `<vc:…>` tag survives into the output.
- **Which instance a section forwards** to a contributor interface follows where the read is served: forward the caching decorator only when the fan-out read projects from its cached snapshot, otherwise the inner scoped service.

## Behavioural notes

- Shared form validation is a Shell concern: `AddHumansInfrastructure` configures MVC's model-binding and numeric client-validation messages through `SharedResource` in all supported cultures, resolved at bind or render time so cached MVC metadata does not freeze the first request's culture. Section annotations use the same resource through the Shell's data-annotation localizer; section-specific business errors stay section-owned.
- Shell UI fallbacks (admin-summary reads, navigation pill counts, bundled About statistics) log unexpected failures at Error with the original exception and keep their omission fallbacks; admin-summary request cancellation propagates.
- The filesystem store logs unexpected save, non-missing read and temporary-file cleanup failures at Error with their exceptions; cleanup preserves the original save failure or cancellation; a file disappearing during a read is a normal miss.
