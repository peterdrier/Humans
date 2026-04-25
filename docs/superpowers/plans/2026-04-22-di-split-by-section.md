# Split `AddHumansInfrastructure` by section

## Problem

`src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs` is a 378-line god-method registering ~60 services across every section of the app. With ~20 PRs in flight, nearly every one adds or changes a service registration and conflicts with the others on this one file.

The conflicts are mechanical (add a line in the same area) but they cascade: every time one PR merges, the next 19 need a rebase. The file size is a side effect — the real cost is the shared write surface.

## Goal

Split the single method into one-file-per-section extension methods so that PRs touching different sections stop conflicting. Zero behavior change. The external entrypoint (`services.AddHumansInfrastructure(configuration, environment, configRegistry)`) keeps the same signature.

## Layout

All new files live under `src/Humans.Web/Extensions/`, with two subfolders:

### `Sections/` — section-owned registrations

Each section file defines `public static IServiceCollection AddXxxSection(this IServiceCollection services)`. Section jobs, section options bindings, section repositories, and section services all live in the section file.

| File | Owns |
|------|------|
| `ProfileSectionExtensions.cs` | Profile repos, Profile/ContactField/UserEmail/CommunicationPreference services, ContactService, CachingProfileService + `IFullProfileInvalidator`, FullProfileWarmupHostedService, Unsubscribe services, DuplicateAccountService, AccountMergeService, AccountProvisioningService, EmailProvisioningService |
| `UsersSectionExtensions.cs` | IUserRepository, UserService, IMembershipCalculator, IDashboardService |
| `AuthSectionExtensions.cs` | RoleAssignmentService, IMagicLinkService |
| `TeamsSectionExtensions.cs` | TeamService, ITeamPageService, SystemTeamSyncJob |
| `GovernanceSectionExtensions.cs` | IApplicationRepository, ApplicationDecisionService, IOnboardingService, nav/voting/notification-meter cache invalidators, TermRenewalReminderJob |
| `CampsSectionExtensions.cs` | CampService, ICampContactService |
| `CityPlanningSectionExtensions.cs` | CityPlanningOptions, ICityPlanningRepository, ICityPlanningService |
| `BudgetSectionExtensions.cs` | BudgetService, ITicketingBudgetService |
| `ShiftsSectionExtensions.cs` | IShiftManagementService, ShiftSignupService, IGeneralAvailabilityService, ICalendarService |
| `TicketsSectionExtensions.cs` | ITicketSyncService, TicketQueryService, TicketSyncJob, TicketingBudgetSyncJob |
| `FeedbackSectionExtensions.cs` | FeedbackService, FeedbackApiSettings + ApiKeyAuthFilter |
| `NotificationsSectionExtensions.cs` | INotificationService, NotificationInboxService, INotificationMeterProvider, CleanupNotificationsJob |
| `LegalAndConsentSectionExtensions.cs` | LegalDocumentSync/Admin/Service, ConsentService, SyncLegalDocumentsJob, SendReConsentReminderJob |
| `CampaignsSectionExtensions.cs` | CampaignService |
| `AuditLogSectionExtensions.cs` | AuditLogService |
| `GdprSectionExtensions.cs` | IGdprExportService |
| `AdminSectionExtensions.cs` | ISyncSettingsService, SuspendNonCompliantMembersJob, ProcessAccountDeletionsJob, SendAdminDailyDigestJob, SendBoardDailyDigestJob, LogApiSettings + LogApiKeyAuthFilter |

### `Infrastructure/` — cross-cutting integrations

| File | Owns |
|------|------|
| `EmailInfrastructureExtensions.cs` | EmailSettings + PostConfigure, SMTP vs Stub transport branching, IEmailRenderer, IEmailService (OutboxEmailService), IEmailOutboxService, ProcessEmailOutboxJob, CleanupEmailOutboxJob |
| `GoogleWorkspaceInfrastructureExtensions.cs` | GoogleWorkspaceSettings, TeamResourceManagementSettings, credentials-vs-stub branching for IGoogleSyncService / ITeamResourceService / IDriveActivityMonitorService / IGoogleWorkspaceUserService, IGoogleAdminService, DriveActivityMonitorJob, GoogleResourceReconciliationJob, ProcessGoogleSyncOutboxJob |
| `TicketVendorInfrastructureExtensions.cs` | TicketVendorSettings, env-branched HttpClient vs StubTicketVendorService |
| `StripeInfrastructureExtensions.cs` | StripeSettings + IStripeService |
| `TelemetryInfrastructureExtensions.cs` | IHumansMetrics, GitHubSettings options binding |
| `ConfigurationMetadataExtensions.cs` | The entire `if (configRegistry is not null)` block + env-var `RegisterEnvironmentVariable` calls |

## Orchestrator

`InfrastructureServiceCollectionExtensions.cs` keeps `AddHumansInfrastructure` as the single public entrypoint. Its body becomes a flat list of section/infrastructure calls in registration order:

```csharp
public static IServiceCollection AddHumansInfrastructure(
    this IServiceCollection services,
    IConfiguration configuration,
    IHostEnvironment environment,
    ConfigurationRegistry? configRegistry = null)
{
    services.AddConfigurationMetadata(configuration, configRegistry);
    services.AddTelemetryInfrastructure(configuration);
    services.AddEmailInfrastructure(configuration, environment);
    services.AddGoogleWorkspaceInfrastructure(configuration, environment);
    services.AddTicketVendorInfrastructure(configuration, environment);
    services.AddStripeInfrastructure();

    services.AddProfileSection();
    services.AddUsersSection();
    services.AddAuthSection();
    services.AddTeamsSection();
    services.AddGovernanceSection();
    services.AddCampsSection();
    services.AddCityPlanningSection(configuration);
    services.AddBudgetSection();
    services.AddShiftsSection();
    services.AddTicketsSection();
    services.AddFeedbackSection();
    services.AddNotificationsSection();
    services.AddLegalAndConsentSection();
    services.AddCampaignsSection();
    services.AddAuditLogSection();
    services.AddGdprSection();
    services.AddAdminSection();

    return services;
}
```

## Constraints

- **No behavior change.** Registrations move verbatim, including comments, registration modifiers (`AddKeyedScoped`, `AddSingleton` vs `AddScoped`), and factory-lambda forwarding patterns like `sp => sp.GetRequiredService<...>()`.
- **No new lifetimes.** Keep each service's existing lifetime exactly. The Profile section's Singleton-over-Scoped forwarding and Keyed registration must be preserved literally.
- **Same call order.** The orchestrator calls infrastructure first (config metadata, email, Google, ticket vendor) then sections. Inside each section, preserve the original ordering to minimize diff review surface.
- **Environment-conditional branches stay local.** `if (environment.IsProduction())` stays inside the file that owns the branch (Email, Google, TicketVendor).
- **Exceptions in production** (`throw new InvalidOperationException(...)`) stay identical.

## Why this reduces PR conflicts

Today: every service registration touches `InfrastructureServiceCollectionExtensions.cs`. 20 PRs → 20 PRs conflict.

After: each PR touches the one section file it affects. Two PRs touching the Profile section still conflict; a Profile PR and a Budget PR no longer do. In practice most in-flight PRs target different sections, so the 20-way conflict collapses to a handful.

## Follow-up

After this merges, a separate `pr-swarm-rebase` run rebases the 20 open PRs and relocates each PR's new registration from the orchestrator's old body into the correct section file. That's a cross-cutting pattern fix the swarm handles mechanically.
