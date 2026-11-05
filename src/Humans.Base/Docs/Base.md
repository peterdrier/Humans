<!-- freshness:triggers
  src/Humans.Base/**
-->
<!-- freshness:flag-on-change
  What Humans.Base holds and why. Flag when a folder, marker, attribute or shared view-layer piece is added, moved or removed.
-->

# Humans.Base — the base

`src/Humans.Base` is the bottom of the dependency graph and the only project every section may reference. The rules it enforces live in [`design-rules.md`](../../../docs/architecture/design-rules.md); this doc says what the project holds and why.

## Invariant

**No `Humans.*` project reference.** Anything every section needs lives here; a type that needs another project's types does not belong here. External dependencies are unrestricted (Peter, 2026-08-15): the base may take any NuGet package or shared framework it needs, and today carries `Microsoft.AspNetCore.App`, EF Core + Npgsql (+ NodaTime provider), NodaTime, CsvHelper, Serilog, Octokit, HtmlSanitizer, Markdig and AngleSharp. Reason about placement from the reference rule and from whether a type is genuinely cross-section, never from package count. The csproj's header comment records which arrival forced each package.

## Namespaces

Types live under `Humans.Base.<folder>`, folder-mirrored (`Humans.Base.Authorization.PolicyNames`, `Humans.Base.Extensions.DateTimeDisplayExtensions`). The section cores that moved down whole keep their section namespace: `Users/` is `Humans.Users.Contracts`, `AuditLog/` and `Gdpr/` likewise. The assembly Razor binds by name is `Humans.Base`, which is what `@addTagHelper *, Humans.Base` names.

## What it holds

| Folder | Holds |
|---|---|
| `Interfaces/` | The role markers (`IApplicationService`, `IRepository` under `Repositories/`, `IOrchestrator`, `IFanout`, `IInvalidator`, `ISection`, `IRecurringJob`); the contribution seams (`ISectionContribution` and every `ISection*` seam, `IUserPart`, with their slot name classes `ChromeSlots` and `UserPartSlots`); `ISectionCatalog`; `IEntityNameContributor`; `IFileStorage`; the stats and metering seams the Shell implements |
| `Attributes/` | The architecture attributes the analyzers key on: `GrandfatheredAttribute`, `DontFixAttribute`, `SurfaceBudgetAttribute`, `ExternalWriteAttribute`, `ExpiresOnAttribute`, `ViewComponentSlotAttribute`, plus `AllowBeforeActiveMembershipAttribute` and `MarkdownContentAttribute` |
| `Caching/` | `TrackedCache<TKey, TValue>`, the primitive every section caching decorator inherits or composes, and `CacheKeys` |
| `Data/`, `Hosting/` | The per-section persistence seam: `AddSectionDbContext<TContext>` and `SectionDbContextRegistration`, `SectionMigrationsHistory.TableFor<TContext>()`, `SectionMigrationRunner` and `DatabaseMigrationHostedService`, the query-monitoring interceptor, `PolledGaugeService` |
| `Users/` | The Users core: the `User` and `UserEmail` entities (their tables and EF mapping stay in `Humans.Users`), `UserInfo`, `IUserService`, `IUserEmailService` and the enums they name. Naming `User` or `UserEmail` outside the base and Users is HUM0039 |
| `AuditLog/` | The audit write face: `IAuditLogService` (`LogAsync` only), `AuditActionDefinition`, `IAuditActionContributor`, `AuditSubjectTypes`. Audit reads live in the AuditLog section |
| `Gdpr/` | `IUserDataContributor` and `UserDataSlice`, the GDPR contributor seam |
| `Authorization/` | `PolicyNames` (the cross-section policies), `RoleChecks`, `IDescribedRequirement` |
| `Configuration/` | `ConfigurationRegistry`, the inventory `ISectionConfiguration` contributions feed |
| `Controllers/` | `HumansControllerBase` |
| `Services/` | The GitHub content sources behind the guide and community KB — the one place Octokit is used |
| `Csv/`, `Logging/`, `Metering/`, `Threading/`, `Diagnostics/`, `Helpers/`, `Extensions/`, `Constants/`, `Enums/` | Section-agnostic utilities: the single CSV seam, Serilog enrichers and the in-memory sink, meters, display extensions, reserved ids |

## The shared view layer

The base builds with `Microsoft.NET.Sdk.Razor` + `AddRazorSupportForMvc` to host it.

- **Localization:** `SharedResource` and its satellite resx files in every supported culture. The member-facing section-help widget's tooltip, close label, tabs and feature header use it; contributed help content and access-matrix rows stay owned by their sections.
- **Tag helpers** (`TagHelpers/`): `AuthorizeView`, `EmptyState`, `MarkdownEditor`, `Nonce`, `PageHeader`, `StatusBadge`.
- **View components** (`ViewComponents/`): the section-agnostic ones — `Human`, `HumanSearch`, `TempDataAlerts`, `AccessMatrix`, `Breadcrumb` — and the composers that render section contributions: `SectionNav`, `ChromeSlot`, `ThingsToDo`, `UserParts`, `AdminBreadcrumb` and `AdminNavComposition` (see [`Shell.md`](../../Humans.Web/Docs/Shell.md) for the seam catalog).
- **Shared partials** (`Views/Shared/`): `_Table`, `_Pager`, `_StatTile`, `_DashboardActionCard`, `_EmailComposer`, `_EmailPreviewModal`, `_MarkdownHelp`, `_TabbedMarkdownDocuments`, `_ValidationScriptsPartial`, `_VolunteerSearchScript`, `_GitHubFolderPathScript`. The chrome (`_Layout`, `_AdminLayout`, `_LoginPartial`, `_LanguageChooser`, `_AuthorizationPill`, `_VersionInfo`) is the Shell's.
- **View models** (`Models/`): the generic table models (`Models/Tables/`), `PagerViewModel`, `PagedListViewModel`, the help and access-matrix models, the human-search picker models.

### Behavioural notes

- Shared dismiss controls (member alerts, Markdown help, login errors, dynamically created toasts) use `Common_Close` for their accessible label; both Shell layouts supply it to the toast helper.
- The human-search picker rejects stale searches: a lookup dismissed with Escape or an outside click is invalidated, timers and response versions belong to each input, and an older response never replaces a newer result.
- The email composer preview is ordered per modal: only the latest preview can replace its frame, subject or error, and separate modal instances stay independent.

## Tests

The architecture tests over the base and the section graph live in `tests/Humans.Web.Tests/Architecture/` (entity ownership per context, view component slot contracts, tag helper binding, the `IMemoryCache` allowlist); `TrackedCache` has its own unit tests in `tests/Humans.Base.Tests/Caching/`.
