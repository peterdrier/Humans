> Snapshot appendix to [2026-08-14-g5-endstate-design.md](2026-08-14-g5-endstate-design.md) — audited 2026-08-14 at origin/main post-#1292. Point-in-time evidence; the decisions doc is the authority.

# 4a inventory — Humans.Infrastructure + Humans.UI

Scope: `src/Humans.Infrastructure`, `src/Humans.UI` in the main checkout, read-only.
GoogleIntegration/Shifts/Camps are already fully carved out of Infrastructure (no
leftover folders found — nothing to flag there). Users/Profiles repositories,
services, `UsersDbContext`(+factory+interceptor) and `Migrations/Users` are lane 2's
scope and are listed under EXCLUDED, not classified.

Legend for **Outbound**: framework-only / Base-clean (no Application or section
types) vs which Application namespaces / section `.Contracts` it touches.

---

## Section-owned

### Humans.Infrastructure

| File(s) | What it is | Inbound consumers | Outbound | Section |
|---|---|---|---|---|
| `Jobs/AgentConversationRetentionJob.cs` | Recurring job | Web's `AddHumansInfrastructure`-family roll-call, Hangfire | `Humans.Agent.Contracts` | Agent |
| `Jobs/CleanupEmailOutboxJob.cs`, `Jobs/ProcessEmailOutboxJob.cs` | Recurring jobs | Web roll-call, Hangfire | `Humans.Email.Contracts` | Email |
| `Services/HangfireImmediateOutboxProcessor.cs` | `IImmediateOutboxProcessor` impl, hardcodes `ProcessEmailOutboxJob` | Web's `EmailInfrastructureExtensions`, `Program.cs` | `Humans.Email.Contracts` + own `Jobs` ns | Email |
| `Configuration/EmailSettings.cs` | Options POCO | Email section (Controller/Section/5 services), Web (Email/TicketVendor extensions, SmtpHealthCheck), + Auth's `MagicLinkUrlBuilder`, Profiles' `UnsubscribeTokenProvider` (settings POCO, cross-section binding is normal) | none | Email |
| `Jobs/CleanupIssuesJob.cs` | Recurring job | Web roll-call | `Humans.Issues.Contracts` | Issues |
| `Jobs/CleanupNotificationsJob.cs` | Recurring job | Web roll-call | `Humans.Notifications.Contracts` | Notifications |
| `Jobs/DriveActivityMonitorJob.cs` | Recurring job | Web roll-call | `Humans.Monitor.Contracts` | Monitor |
| `Jobs/GateRetentionJob.cs`, `Jobs/GateVendorCheckInJob.cs` | Recurring jobs | Web roll-call | `Humans.Gate.Contracts` (+ `Tickets.Contracts` for vendor mirroring in the latter) | Gate |
| `Jobs/GoogleResourceProvisionJob.cs`, `GoogleResourceReconciliationJob.cs`, `ProcessGoogleSyncOutboxJob.cs` | Recurring jobs | Web roll-call | `Humans.GoogleIntegration.Contracts` | GoogleIntegration |
| `Configuration/GoogleWorkspaceSettings.cs` | Options POCO | GoogleIntegration section (Section.cs + 8 Workspace/*Client.cs), Web (extensions, health check) | none | GoogleIntegration |
| `Jobs/MailerAudienceSyncJob.cs` | Recurring job | Web roll-call | `Humans.Mailer.Contracts` | Mailer |
| `Jobs/ProcessAccountDeletionsJob.cs` | Recurring job | Web roll-call | `AuditLog.Contracts`, `Email.Contracts`, `Application.Interfaces.Users`, `Domain.Entities/Enums` | Users (borderline — a job, not a "repository/service", so kept in this inventory rather than lane 2's exclude list; flag for Peter) |
| `Jobs/SendReConsentReminderJob.cs`, `Jobs/SyncLegalDocumentsJob.cs` | Recurring jobs | Web roll-call | `Consent.Contracts` (+ `Governance.Contracts`, `Email.Contracts`) | Consent |
| `Jobs/SendSurveyReminderJob.cs` | Recurring job | Web roll-call | `Surveys.Contracts` | Surveys |
| `Jobs/TermRenewalReminderJob.cs` | Recurring job | Web roll-call | `Governance.Contracts` (+ `Email`, `Notifications`) | Governance |
| `Jobs/TicketSyncJob.cs` | Recurring job | Web roll-call | `Tickets.Contracts` | Tickets |
| `Jobs/TicketingBudgetSyncJob.cs` | Recurring job | Web roll-call | `Budget.Contracts` | Budget |
| `Jobs/HoldedExpenseOutboxJob.cs` | Recurring job | Web roll-call | `Expenses.Contracts` + `Services/Holded` | Expenses |
| `Services/Holded/HoldedClient.cs`, `HoldedCallLog.cs`, `HoldedClientOptions.cs` | Holded API HTTP client + call log + options | `Jobs/HoldedExpenseOutboxJob`, `Jobs/HoldedSyncJob` | `Application.Interfaces.Holded` only | Holded (`Humans.Holded` section already exists and owns the ledger-mirror domain this client feeds) |
| `Services/StoreWebhookRegistrationService.cs` | Registers Store's Stripe webhooks at boot | Web's `StripeInfrastructureExtensions` only | `Application.Configuration` | Store |
| `Services/Auth/MagicLinkRateLimiter.cs`, `MagicLinkUrlBuilder.cs` | Magic-link auth helpers | Auth's own `Section.cs`, Web's `AuthSectionExtensions` | `Application.Interfaces.Auth` | Auth |
| `Services/GitHubGuideContentSource.cs`, `Configuration/GuideSettings.cs` | GitHub-markdown content fetcher + its options | See Base row below — **code's own doc comment overrides an intuitive "Guide" placement** | — | see Base |
| `Services/GitHubCommunityKbContentSource.cs`, `Configuration/CommunityKbSettings.cs` | Same pattern, community-KB flavor | Agent's `Section.cs`, Guide's `Section.cs` | — | see Base |

### Humans.UI

| File(s) | What it is | Inbound consumers | Outbound | Section |
|---|---|---|---|---|
| `ViewComponents/AuditLogViewComponent.cs` + `Views/Shared/Components/AuditLog/*.cshtml` (3) | Renders an entity/user's audit trail | Cross-section, invoked by name (`<vc:audit-log>`) | `Application.Interfaces.AuditLog` (`IAuditViewerService`) — the concrete `AuditViewerService` implementation stays OUT of AuditLog per Web's own DI comment (see AuditLog Contracts note below) | AuditLog — **matches design note**, contingent on `IAuditViewerService` itself relocating to `Humans.AuditLog.Contracts` (lane 4a's call) |
| `ViewComponents/HumanSearchViewComponent.cs`, `HumanViewComponent.cs`, `ViewComponents/ProfileCardViewMode.cs`, `Views/Shared/Components/Human/Default.cshtml`, `.../HumanSearch/Default.cshtml`, `Views/Shared/_HumanSearchResults.cshtml`, `_VolunteerSearchScript.cshtml` | Person search/display components | Cross-section, invoked by name (Gate, Teams, etc.) | `Application.Interfaces.Users` (`IUserServiceRead`) | Users — **matches design note** |
| `Models/HumanSearchPickerViewModel.cs`, `Models/HumanSearchScope.cs` | Private model + enum for `HumanSearchViewComponent`'s `InvokeAsync` signature | Zero consumers outside `Humans.UI` (`HumanSearchScope` also appears in Web's WidgetGallery demo page, not a real dependency) | none | Users — **addition**: design note names the component but not these two model files that travel with it |
| `Models/ShiftsSummaryCardViewModel.cs`, `Authorization/ShiftRoleChecks.cs`, `Views/Shared/_ShiftsSummaryCard.cshtml` | Shift dashboard card + its role checks | Shifts pages | `Domain.Constants` only | Shifts — **matches design note** |
| `Models/VolunteerBadgesViewModel.cs`, `Views/Shared/_VolunteerProfileBadges.cshtml` | Volunteer profile badge row | Only `Humans.Shifts/Models/ShiftAdminPageBuilder.cs` + `ShiftViewModels.cs` (Web's WidgetGallery demo page is the only other reference) | none | Shifts — **addition**: not named in the design note, but the only real consumer is Shifts |
| `Models/TranslationsGalleryViewModel.cs` | Debug's translation-coverage gallery model | Only `Humans.Debug/Views/Debug/Translations.cshtml` | none | Debug — **addition**: single real consumer, not generic |
| `Hubs/CityPlanningHub.cs` | SignalR presence hub for the barrio/container maps | `MapHub<CityPlanningHub>` named directly in Shell (Web) | `Application.Interfaces.Users` (`IUserServiceRead`), `Domain.Entities` (`User`) | **MISMATCH — see below, not simply "CityPlanning"** |

---

## Base

### Humans.Infrastructure

| File(s) | What it is | Outbound | Notes |
|---|---|---|---|
| `Caching/MemoryCacheInvalidators.cs` — 6 of 7 classes: `NavBadgeCacheInvalidator`, `NotificationMeterCacheInvalidator`, `VotingBadgeCacheInvalidator`, `IssuesBadgeCacheInvalidator`, `CampLeadJoinRequestsBadgeCacheInvalidator`, `RoleAssignmentClaimsCacheInvalidator` | `IMemoryCache`-backed cross-cutting invalidators | `Application.Interfaces.Caching` + extension methods only | Web's own DI comment literally calls these "Base's own" invalidators |
| `Configuration/GuideSettings.cs`, `CommunityKbSettings.cs`, `Services/GitHubGuideContentSource.cs`, `Services/GitHubCommunityKbContentSource.cs` | GitHub-markdown content-fetch abstraction + 2 connectors | Guide, Agent (3 preload readers), Web's `AgentDocsHealthCheck` | `Guide/Section.cs`'s own doc comment: "the interface... is a plain GitHub-markdown fetcher whose signatures name nothing but `string`... stays in Base with the settings type it binds." `Agent/Section.cs` independently confirms `GitHubCommunityKbContentSource` "stays in Humans.Infrastructure [→ Base]: it is a GitHub content connector implementing a Base interface." |
| `Data/QueryMonitoringInterceptor.cs`, `Data/QueryStatistics.cs` | EF Core command interceptor + in-memory stats | Framework-only | Consumed generically by every section context via the shared `AddSectionDbContext<T>` |
| `Data/SectionMigrationsHistory.cs` | Per-context migrations-history table naming helper | Framework-only | Shared by runtime registration and every section's design-time factory |
| `Hosting/SectionDbContextRegistration.cs`, `SectionMigrationRunner.cs`, `DatabaseMigrationHostedService.cs`, `PreMigrationSnapshot.cs` | Generic per-section EF Core registration + startup migration runner | Framework-only | `AddSectionDbContext<T>()` (in `InfrastructureServiceCollectionExtensions.cs`, see MISMATCH) is called from **every** section's `Section.cs` — must be reachable from section projects, i.e. must be Base |
| `Hosting/InfrastructureServiceCollectionExtensions.cs` — **only** the generic `AddSectionDbContext<TContext>()` method + private `ConfigureNpgsql` helper | Generic per-context DI wiring | Framework-only | See MISMATCH below — the rest of this file does not belong here |
| `Logging/CurrentUserEnricher.cs`, `InMemoryLogSink.cs`, `PiiRedactionEnricher.cs` | Serilog enrichers/sink | Framework-only (`System.Security.Claims`, `Microsoft.AspNetCore.Http`, Serilog) | No section coupling |
| `Helpers/HtmlPlainTextConverter.cs` | HTML→plain-text string utility | Framework-only | — |
| `Services/Metering/MetersService.cs` | `IMeters`/`IMeter` counter service | `Application.Interfaces.Metering`, `Application.Metering` only | Consumed by Email's `EmailOutboxProcessor` and Web's telemetry wiring — no section owns the abstraction, no section-type coupling either |
| `Services/EarlyEntry/CachingEarlyEntryService.cs` | Singleton caching decorator over `IEarlyEntryService` | `Application.Interfaces.Caching`, `Application.Interfaces.EarlyEntry` only — zero section types | Contingent on those two interfaces also landing outside a section (lane 4a call); no `EarlyEntry` section exists — Camps/Shifts/Teams all consume the underlying feature |

### Humans.UI

| File(s) | What it is | Notes |
|---|---|---|
| `Resources/SharedResource.*` (7 files: .cs + 6 .resx) | Shared string resources | Matches design note |
| `Models/Tables/*.cs` (5: `EnumBadgeMap`, `ITableModel`, `TableColumn`, `TableEnums`, `TableModel`), `Views/Shared/_Table.cshtml` | Generic table rendering | Matches design note. `EnumBadgeMap.cs` uses `Domain.Enums` — fine if those enums are themselves Base-bound primitives (flag: contingent on Domain's split, lane 4a) |
| `Models/PagedListViewModel.cs`, `PagerViewModel.cs`, `Views/Shared/_Pager.cshtml` | Generic pager | Matches design note; heavily multi-section consumed (AuditLog, Governance, Teams, Tickets, Web) |
| `TagHelpers/NonceTagHelper.cs`, `PageHeaderTagHelper.cs`, `MarkdownEditorTagHelper.cs`, `AuthorizeViewTagHelper.cs` | Generic tag helpers | Matches design note |
| `ViewComponents/TempDataAlertsViewComponent.cs`, `Constants/TempDataKeys.cs`, `Views/Shared/Components/TempDataAlerts/Default.cshtml` | Generic TempData alert rendering | Matches design note |
| `Extensions/CultureCodeExtensions.cs`, `DateTimeDisplayExtensions.cs`, `EnumLocalizationExtensions.cs`, `HtmlHelperExtensions.cs`, `PageSizeExtensions.cs`, `StringSearchExtensions.cs` | Generic extension methods | No section types (`DateTimeDisplayExtensions` uses `Application.Extensions` — generic culture/NodaTime helpers, verify at move time) |
| `Extensions/PersonSearchOrderingExtensions.cs`, `SearchResultMappingExtensions.cs` | Cross-section search-result ordering/mapping | Own doc comments: "In Humans.UI rather than Shell because a section project cannot reference Humans.Web" and both have 2+ cross-section consumers (Gate/Search/Profile) |
| `Models/SearchResponseModels.cs` (`RoleAssignmentSearchResult`, `BurnerNameCountResult`) | Generic JSON row shapes | Own doc comment confirms: Teams' admin controller returns one of these and "a section cannot reference Humans.Web" |
| `Models/HumanLookupSearchResult.cs` | Generic person-lookup row | Real multi-section consumers: Gate, Teams, Web's `ProfileApiController` |
| `Models/HumanSearchResultViewModel.cs` | Generic search-result view row | Consumers: Search section + Web widely (Admin views, WidgetGallery) |
| `Authorization/PolicyNames.cs`, `RoleChecks.cs` | Canonical policy-name constants + role-check predicates | `RoleChecks`' own doc comment: "A section cannot name a Humans.Web type, and the predicate carries no section vocabulary" (design §15 step 6) |
| `Filters/ApiKeyAuthFilterBase.cs` | Shared `X-Api-Key` auth filter base | Own doc comment: sections (Feedback, Issues, Agent, Surveys) each own a `<Section>ApiKeyAuthFilter` subclass, and "a section cannot reference Humans.Web" |
| `Models/AssigneeOption.cs` | Generic assignee dropdown option | 2-section consumer (Feedback, Issues), no section vocabulary in shape — **not named in design note** |
| `Models/TabbedMarkdownDocumentsViewModel.cs`, `Views/Shared/_TabbedMarkdownDocuments.cshtml` | Generic tabbed-markdown display | 2-section consumer (Consent, Governance) — **not named in design note** |
| `Models/FavouriteButtonModel.cs`, `Views/Shared/_FavouriteButton.cshtml` | Generic favourite-heart toggle | File's own doc comment explicitly asserts Base intent ("this model and its partial are Humans.UI — Base, which must not reference a section... resource-neutral for the next section that needs a favourite heart") — **but today only Events consumes it**, and the model field is literally named `EventId`, not a generic `ItemId`. Flag: generic-by-intent, single-consumer-by-fact, and the field name already leaks Events vocabulary |
| `Views/Shared/_MarkdownHelp.cshtml`, `_EscapeHtmlScript.cshtml`, `_GitHubFolderPathScript.cshtml`, `_RequestVerificationTokenScript.cshtml`, `_ValidationScriptsPartial.cshtml`, `_VersionInfo.cshtml`, `_AuthorizationPill.cshtml`, `_RoleBadge.cshtml`, `_LanguageChooser.cshtml` | Generic script/markup partials | No section vocabulary found; not individually deep-audited beyond a name/content skim — spot-check before the actual move |
| `Views/_ViewImports.cshtml` | Global Razor imports for the RCL | **Needs rework, not just a move**: currently imports `Humans.Application.Extensions`, `Humans.Domain.Entities`, `Humans.Domain.Enums` — both projects are deleted at G5 end state, so these usings must be re-pointed once Application/Domain's split lands (lane 4a) |

---

## MISMATCHES (design-note assignment vs. actual code)

1. **`Controllers/HumansControllerBase.cs` + `Controllers/ApiControllerBase.cs`** — design note lists both as Base ("generic view plumbing"). Both take **`IUserServiceRead userService`** as a primary-constructor dependency and store it as a protected member (`GetCurrentUserInfoAsync`, `FindUserInfoByIdAsync`, `ResolveCurrentUser…Async` all call into it). `IUserServiceRead` is a Users-section cross-section read interface. Base's own charter is explicit: *"Guard: nothing in Base references a section — compiler-enforced... Rule: no SECTION types, ever."* As written, these two classes cannot compile inside Base once Users' interfaces move to `Humans.Users.Contracts`. This is load-bearing — essentially every MVC/API controller in every section inherits one of these two for current-user resolution + TempData alerts. Needs a real decision, not a mechanical file move: e.g. resolve the user lazily via `HttpContext.RequestServices` (untyped) instead of a typed ctor param, or accept these live somewhere sections can all reach that isn't pure Base. Flagging for Peter rather than picking unilaterally, since it affects the base-class shape every section's controllers depend on.

2. **`Caching/MemoryCacheInvalidators.cs`** — one file, 7 classes. 6 are Base-clean (see Base table). The 7th, `ActiveTeamsCacheInvalidator`, takes `ITeamService` from `Humans.Teams.Contracts` directly — violates Base's "no section types" rule if left in the same file/project. Needs splitting: 6 classes → Base, `ActiveTeamsCacheInvalidator` → Teams (it just forwards to the section's own service, a natural fit there).

3. **`Hosting/InfrastructureServiceCollectionExtensions.cs`** — one file mixes the generic `AddSectionDbContext<TContext>()` extension (Base-clean, called by every section) with `AddHumansPersistence()` (calls `AddSectionDbContext<UsersDbContext>` and `<SystemDbContext>` directly — concrete section/horizontal types) and two typed wrappers (`AddHumansEntityFrameworkStores`, `PersistKeysToSystemDbContext`) that also name `UsersDbContext`/`SystemDbContext` directly. Needs splitting the same way as #2: the generic method → Base, the three UsersDbContext/SystemDbContext-naming members → wherever those two contexts' registration lands (most likely Web's roll-call, next to `AddUsersSection()`).

4. **`Hubs/CityPlanningHub.cs`** — design note assigns this to CityPlanning. The file's **own doc comment contradicts that**: it says it lives in `Humans.UI` today "for the same reason `ApiKeyAuthFilterBase` is" — but `ApiKeyAuthFilterBase`'s actual reason (sections need to *inherit* from it, and can't reference Web) is different from `CityPlanningHub`'s actual reason (Shell's `app.MapHub<CityPlanningHub>("/hubs/city-planning")` names the **concrete type**, and HUM0034 forbids a section assembly from exposing a public type). The comment then explicitly says the eventual fix is "the Gate `<vc:human-search>` shape, **not a promotion of section types into Base**" — i.e. it should *not* just move to Base either. Read literally, that means: today's `MapHub<T>` mechanism can't reach a type living inside the CityPlanning section assembly at all, public or not, without Shell taking a compile-time reference to it (which HUM0034 blocks). Moving this into CityPlanning as the design note proposes only works once there's an `ISection`-level endpoint-mapping seam (so CityPlanning's own `Section.cs` calls `endpoints.MapHub<CityPlanningHub>(...)` from inside its own assembly, no Web reference needed). Until that seam exists, this has to stay outside the section — Base or Web, not CityPlanning.

---

## UNDECIDED

| File(s) | Two defensible options | Tension |
|---|---|---|
| `Jobs/SuspendNonCompliantMembersJob.cs` | Own section(s) vs. deliberate Host exception | Explicitly named in the design doc's open agenda — reads GoogleIntegration, AuditLog, Email, Governance, Notifications, Shifts, Teams and Users contracts in one job; no single section can plausibly own it |
| `Jobs/SystemTeamSyncJob.cs` (`ISystemTeamSync` impl) | GoogleIntegration (the interface it implements) vs. Base vs. Host exception | Explicitly named in the design doc's open agenda. Web's own DI comment calls it (alongside `ActiveTeamsCacheInvalidator`) a "Base collaborator," but it reaches into 7 different section `.Contracts` leaves (GoogleIntegration, Auth, AuditLog, Email, Governance, Teams, Camps) — too broad for Base's "no section types" rule as written |
| `Jobs/HoldedSyncJob.cs` | Finance vs. Holded | Own doc comment: "Nightly Holded pull: purchase docs (Finance) then the ledger mirror (Holded section)" — it orchestrates both sections' syncs as one Hangfire job; no single owner |
| `Data/SystemDbContext.cs`, `SystemDbContextFactory.cs`, `Migrations/System/*` (3 files) | Base vs. Web | Design doc names this exact question as still open. `SystemDbContext` itself is compiler-legal for Base (zero section deps, only `DataProtectionKeys`), but its concrete registration call lives inside the file split at MISMATCH #3, which itself needs a Web-ish home — the DbContext class and its registration call don't have to land in the same tier, but it'd be odd to split them |
| `Services/AdminDatabaseDiagnosticsService.cs`, `Repositories/Admin/AdminDatabaseDiagnosticsRepository.cs` | SystemSettings vs. a dedicated Host-level diagnostics capability | Repository directly opens `UsersDbContext` AND iterates every registered `SectionDbContextRegistration` to report migration status for the whole app; service also reads `Tickets.Contracts` for audience segmentation. `/Admin/*` is documented as "a nav holder, not a section — its services belong to the sections they act on," but this one genuinely acts on the whole platform, not one section |
| `Services/StripeService.cs`, `Services/StripeStartupSmokeService.cs` | Store vs. a shared payments leaf | Store, Finance, and Tickets sections all call `IStripeService` directly (Store's webhook controller/service, Finance's `Section.cs`, Tickets' `TicketSyncService`) — no single natural owner, and inventing a new "Payments" section conflicts with reuse-first guidance against purpose-built new projects |
| `Services/HumansMetricsService.cs` | Monitor vs. Web-resident boot infra | Reads GoogleIntegration/Auth/Users/Consent/Teams/Governance contracts to compute OpenTelemetry gauges, but is registered as an `AddHostedService` directly in Web's `TelemetryInfrastructureExtensions`/`Program.cs`, the same way a health check would be, not through any section's own DI file |

---

## Housekeeping notes

- No `GoogleIntegration`, `Shifts`, or `Camps` leftover folders exist in `Humans.Infrastructure` — all three were fully carved out in prior PRs (#1289, #1291, and the Shifts move referenced in `main`'s recent history). Nothing to flag.
- `Data/Configurations/Profiles/*.cs` (6 files) and `Data/Configurations/Users/*.cs` (3 files) are **not individually named** in lane 2's exclude list, but `UsersDbContext.OnModelCreating` is the only caller of all 9 `ApplyConfiguration(...)` calls — they are structurally inseparable from `UsersDbContext` and were folded into the EXCLUDED bucket rather than classified here. Flagging in case lane 2 didn't already assume it owns them.
- `Repositories/Users/*.cs` (4 files), `Repositories/Profiles/CommunicationPreferenceRepository.cs`, `Services/Users/CachingUserService.cs` + `IUserInfoSliceRefresher.cs`, `Services/Profiles/UnsubscribeTokenProvider.cs` — all EXCLUDED per lane 2's scope, confirmed by inspection (all reference `UsersDbContext`/`Application.Interfaces.Users`/`Application.Interfaces.Profiles`).
