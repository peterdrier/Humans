using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Gdpr;
using Humans.Infrastructure.Caching;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.HostedServices;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Services;
using Humans.Infrastructure.Services.Profiles;
using Humans.Web.Filters;
using GovernanceApplicationDecisionService = Humans.Application.Services.Governance.ApplicationDecisionService;
using ProfilesProfileService = Humans.Application.Services.Profile.ProfileService;
using ProfilesContactFieldService = Humans.Application.Services.Profile.ContactFieldService;
using ProfilesUserEmailService = Humans.Application.Services.Profile.UserEmailService;
using ProfilesCommunicationPreferenceService = Humans.Application.Services.Profile.CommunicationPreferenceService;
using ProfilesContactService = Humans.Application.Services.Profile.ContactService;
using UsersUserService = Humans.Application.Services.Users.UserService;
using CityPlanningCityPlanningService = Humans.Application.Services.CityPlanning.CityPlanningService;

namespace Humans.Web.Extensions;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddHumansInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        ConfigurationRegistry? configRegistry = null)
    {
        services.Configure<GitHubSettings>(configuration.GetSection(GitHubSettings.SectionName));
        services.Configure<EmailSettings>(configuration.GetSection(EmailSettings.SectionName));
        services.PostConfigure<EmailSettings>(settings =>
        {
            if (settings.FromAddress.Contains("noreply", StringComparison.OrdinalIgnoreCase))
            {
                // Log at startup so operators notice the misconfiguration immediately.
                // This uses Console.Error because ILogger isn't available during DI setup.
                Console.Error.WriteLine(
                    $"WARNING: Email:FromAddress is set to '{settings.FromAddress}'. " +
                    "System emails should come from 'humans@nobodies.team'. " +
                    "Check Coolify environment variable override.");
            }
        });
        services.Configure<GoogleWorkspaceSettings>(configuration.GetSection(GoogleWorkspaceSettings.SectionName));
        services.Configure<TeamResourceManagementSettings>(configuration.GetSection(TeamResourceManagementSettings.SectionName));
        services.Configure<CityPlanningOptions>(configuration.GetSection(CityPlanningOptions.SectionName));

        // Register all infrastructure config keys in the registry for the Admin Configuration page
        if (configRegistry is not null)
        {
            // Email settings
            configuration.GetRequiredSetting(configRegistry, "Email:SmtpHost", "Email");
            configuration.GetOptionalSetting(configRegistry, "Email:Username", "Email", isSensitive: true,
                importance: ConfigurationImportance.Recommended);
            configuration.GetOptionalSetting(configRegistry, "Email:Password", "Email", isSensitive: true,
                importance: ConfigurationImportance.Recommended);
            configuration.GetRequiredSetting(configRegistry, "Email:FromAddress", "Email");
            configuration.GetRequiredSetting(configRegistry, "Email:BaseUrl", "Email");
            configuration.GetOptionalSetting(configRegistry, "Email:DpoAddress", "Email",
                importance: ConfigurationImportance.Recommended);

            // GitHub settings
            configuration.GetRequiredSetting(configRegistry, "GitHub:Owner", "GitHub");
            configuration.GetRequiredSetting(configRegistry, "GitHub:Repository", "GitHub");
            configuration.GetRequiredSetting(configRegistry, "GitHub:AccessToken", "GitHub", isSensitive: true);

            // Google Maps
            configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);

            // Google Workspace
            configuration.GetOptionalSetting(configRegistry, "GoogleWorkspace:ServiceAccountKeyPath", "Google Workspace",
                importance: ConfigurationImportance.Recommended);
            configuration.GetOptionalSetting(configRegistry, "GoogleWorkspace:ServiceAccountKeyJson", "Google Workspace",
                isSensitive: true, importance: ConfigurationImportance.Recommended);
            configuration.GetOptionalSetting(configRegistry, "GoogleWorkspace:Domain", "Google Workspace",
                importance: ConfigurationImportance.Recommended);
            configuration.GetOptionalSetting(configRegistry, "GoogleWorkspace:CustomerId", "Google Workspace",
                importance: ConfigurationImportance.Recommended);

            // Ticket Vendor
            configuration.GetOptionalSetting(configRegistry, "TicketVendor:EventId", "Ticket Vendor");
            configuration.GetOptionalSetting(configRegistry, "TicketVendor:Provider", "Ticket Vendor");
            configuration.GetOptionalSetting(configRegistry, "TicketVendor:SyncIntervalMinutes", "Ticket Vendor");

            // Dev auth
            configuration.GetOptionalSetting(configRegistry, "DevAuth:Enabled", "Development");

            // Environment variable secrets
            configRegistry.RegisterEnvironmentVariable("FEEDBACK_API_KEY", "Feedback API", isSensitive: true);
            configRegistry.RegisterEnvironmentVariable("TICKET_VENDOR_API_KEY", "Ticket Vendor", isSensitive: true,
                importance: ConfigurationImportance.Recommended);
            configRegistry.RegisterEnvironmentVariable("LOG_API_KEY", "Log API", isSensitive: true);
            configRegistry.RegisterEnvironmentVariable("STRIPE_API_KEY", "Stripe", isSensitive: true);
        }

        services.AddScoped<ISyncSettingsService, SyncSettingsService>();

        services.AddSingleton<IHumansMetrics, HumansMetricsService>();

        // Contributor-bearing services: register the concrete type once, then
        // forward both the primary interface and IUserDataContributor to that
        // single scoped instance so IGdprExportService can resolve every slice
        // via IEnumerable<IUserDataContributor>.
        services.AddScoped<TeamService>();
        services.AddScoped<ITeamService>(sp => sp.GetRequiredService<TeamService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<TeamService>());

        services.AddScoped<ITeamPageService, TeamPageService>();

        services.AddScoped<CampService>();
        services.AddScoped<ICampService>(sp => sp.GetRequiredService<CampService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CampService>());

        // City Planning section — repository + Application-layer service (§15 migration, PR #543).
        // Small admin-facing section, no caching decorator warranted. Cross-section reads
        // (camps, teams, profiles, users) route through the owning service interfaces.
        services.AddSingleton<ICityPlanningRepository, CityPlanningRepository>();
        services.AddScoped<ICityPlanningService, CityPlanningCityPlanningService>();
        services.AddScoped<ICampContactService, CampContactService>();
        // Profile section — repository/store/decorator pattern (§15 Step 0, PR #504)
        // Repositories use IDbContextFactory and are registered as Singleton so the
        // CachingProfileService Singleton can inject them directly without scope-factory indirection.
        services.AddSingleton<IProfileRepository, ProfileRepository>();
        services.AddSingleton<IContactFieldRepository, ContactFieldRepository>();
        services.AddSingleton<IUserEmailRepository, UserEmailRepository>();
        services.AddSingleton<ICommunicationPreferenceRepository, CommunicationPreferenceRepository>();

        services.AddScoped<IUnsubscribeTokenProvider, UnsubscribeTokenProvider>();

        services.AddScoped<ICommunicationPreferenceService, ProfilesCommunicationPreferenceService>();
        services.AddScoped<IUnsubscribeService, UnsubscribeService>();

        services.AddScoped<CampaignService>();
        services.AddScoped<ICampaignService>(sp => sp.GetRequiredService<CampaignService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CampaignService>());

        services.AddScoped<IContactFieldService, ProfilesContactFieldService>();
        services.AddScoped<IUserEmailService, ProfilesUserEmailService>();
        services.AddScoped<IEmailProvisioningService, EmailProvisioningService>();
        services.AddScoped<ILegalDocumentSyncService, LegalDocumentSyncService>();
        services.AddScoped<IAdminLegalDocumentService, AdminLegalDocumentService>();
        services.AddScoped<ILegalDocumentService, LegalDocumentService>();

        var googleWorkspaceConfig = configuration.GetSection(GoogleWorkspaceSettings.SectionName);
        var hasGoogleCredentials = !string.IsNullOrEmpty(googleWorkspaceConfig["ServiceAccountKeyPath"]) ||
                                   !string.IsNullOrEmpty(googleWorkspaceConfig["ServiceAccountKeyJson"]);

        if (hasGoogleCredentials)
        {
            services.AddScoped<IGoogleSyncService, GoogleWorkspaceSyncService>();
            services.AddScoped<ITeamResourceService, TeamResourceService>();
            services.AddScoped<IDriveActivityMonitorService, DriveActivityMonitorService>();

            services.AddScoped<IGoogleWorkspaceUserService, GoogleWorkspaceUserService>();
        }
        else if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "Google Workspace credentials are required in production. " +
                "Set GoogleWorkspace:ServiceAccountKeyPath or GoogleWorkspace:ServiceAccountKeyJson.");
        }
        else
        {
            services.AddScoped<IGoogleSyncService, StubGoogleSyncService>();
            services.AddScoped<ITeamResourceService, StubTeamResourceService>();
            services.AddScoped<IDriveActivityMonitorService, StubDriveActivityMonitorService>();
            services.AddScoped<IGoogleWorkspaceUserService, StubGoogleWorkspaceUserService>();
        }

        var hasSmtpConfig = !string.IsNullOrEmpty(configuration["Email:SmtpHost"]);

        if (hasSmtpConfig)
        {
            services.AddScoped<IEmailTransport, SmtpEmailTransport>();
        }
        else if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "Email SMTP configuration is required in production. Set Email:Host.");
        }
        else
        {
            services.AddScoped<IEmailTransport, StubEmailTransport>();
        }

        services.AddScoped<IEmailRenderer, EmailRenderer>();
        services.AddScoped<IEmailService, OutboxEmailService>();
        services.AddScoped<IEmailOutboxService, EmailOutboxService>();
        services.AddScoped<IMembershipCalculator, MembershipCalculator>();

        services.AddScoped<RoleAssignmentService>();
        services.AddScoped<IRoleAssignmentService>(sp => sp.GetRequiredService<RoleAssignmentService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<RoleAssignmentService>());

        services.AddScoped<AuditLogService>();
        services.AddScoped<IAuditLogService>(sp => sp.GetRequiredService<AuditLogService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<AuditLogService>());

        services.AddScoped<AccountMergeService>();
        services.AddScoped<IAccountMergeService>(sp => sp.GetRequiredService<AccountMergeService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<AccountMergeService>());

        services.AddScoped<IDuplicateAccountService, DuplicateAccountService>();
        services.AddScoped<IContactService, ProfilesContactService>();
        services.AddScoped<IAccountProvisioningService, AccountProvisioningService>();
        services.AddScoped<IMagicLinkService, MagicLinkService>();

        services.AddScoped<FeedbackService>();
        services.AddScoped<IFeedbackService>(sp => sp.GetRequiredService<FeedbackService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<FeedbackService>());

        services.AddScoped<BudgetService>();
        services.AddScoped<IBudgetService>(sp => sp.GetRequiredService<BudgetService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<BudgetService>());

        services.AddScoped<ITicketingBudgetService, TicketingBudgetService>();

        // Governance — repository + service, no caching decorator.
        // Governance is low-traffic enough that DB reads per request are fine;
        // the service invalidates nav/notification/voting badge caches inline
        // after successful writes.
        services.AddScoped<IApplicationRepository, ApplicationRepository>();

        services.AddScoped<INavBadgeCacheInvalidator, NavBadgeCacheInvalidator>();
        services.AddScoped<INotificationMeterCacheInvalidator, NotificationMeterCacheInvalidator>();
        services.AddScoped<IVotingBadgeCacheInvalidator, VotingBadgeCacheInvalidator>();

        services.AddScoped<GovernanceApplicationDecisionService>();
        services.AddScoped<IApplicationDecisionService>(sp => sp.GetRequiredService<GovernanceApplicationDecisionService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<GovernanceApplicationDecisionService>());

        services.AddScoped<IOnboardingService, OnboardingService>();

        services.AddScoped<ConsentService>();
        services.AddScoped<IConsentService>(sp => sp.GetRequiredService<ConsentService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ConsentService>());

        // ProfileService (inner): Scoped — has many Scoped cross-section deps.
        // Registered under the keyed "profile-inner" key so CachingProfileService can
        // resolve it from a scope without triggering self-resolution on the unkeyed
        // IProfileService registration (which maps to the Singleton decorator).
        services.AddKeyedScoped<IProfileService, ProfilesProfileService>(CachingProfileService.InnerServiceKey);
        services.AddScoped<ProfilesProfileService>(sp =>
            (ProfilesProfileService)sp.GetRequiredKeyedService<IProfileService>(CachingProfileService.InnerServiceKey));
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ProfilesProfileService>());

        // CachingProfileService: Singleton so the _byUserId ConcurrentDictionary persists
        // across requests. Resolves the Scoped inner IProfileService (keyed "profile-inner")
        // and other Scoped deps (IUserService, INavBadgeCacheInvalidator,
        // INotificationMeterCacheInvalidator) per-call via IServiceScopeFactory to avoid
        // the captured-scoped-dep anti-pattern.
        // IProfileRepository and IUserEmailRepository are injected directly because they
        // are also Singleton (IDbContextFactory-based).
        services.AddSingleton<CachingProfileService>();
        services.AddSingleton<IProfileService>(sp => sp.GetRequiredService<CachingProfileService>());

        // CRITICAL: IFullProfileInvalidator must resolve to the same Singleton decorator instance
        // that backs IProfileService. Both interfaces share the single CachingProfileService
        // instance, so the _byUserId dict is never split.
        services.AddSingleton<IFullProfileInvalidator>(sp =>
            sp.GetRequiredService<CachingProfileService>());

        // Eagerly warm the FullProfile dict at startup so bulk reads
        // (birthday widget, location directory, admin human list, profile search)
        // return complete results immediately after deploy instead of filling
        // in lazily per user. Failures are logged and swallowed; lazy population
        // still works.
        services.AddHostedService<FullProfileWarmupHostedService>();

        // User section — §15 repository pattern (issue #511).
        // No decorator / cache: User is ~500 rows with no stitched projection or
        // hot bulk-read path; see docs/superpowers/specs/2026-04-21-issue-511-user-migration.md
        // for the Option A rationale. IUserRepository is Singleton
        // (IDbContextFactory-based) so the service can inject it directly.
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddScoped<UsersUserService>();
        services.AddScoped<IUserService>(sp => sp.GetRequiredService<UsersUserService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<UsersUserService>());
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<ISystemTeamSync, SystemTeamSyncJob>();
        services.AddScoped<SyncLegalDocumentsJob>();
        services.AddScoped<SendReConsentReminderJob>();
        services.AddScoped<ProcessAccountDeletionsJob>();
        services.AddScoped<SuspendNonCompliantMembersJob>();
        services.AddScoped<GoogleResourceReconciliationJob>();
        services.AddScoped<DriveActivityMonitorJob>();
        services.AddScoped<ProcessGoogleSyncOutboxJob>();
        services.AddScoped<ProcessEmailOutboxJob>();
        services.AddScoped<CleanupEmailOutboxJob>();
        services.AddScoped<TicketSyncJob>();
        services.AddScoped<TicketingBudgetSyncJob>();
        services.AddScoped<SendAdminDailyDigestJob>();
        services.AddScoped<SendBoardDailyDigestJob>();
        services.AddScoped<TermRenewalReminderJob>();
        services.AddScoped<CleanupNotificationsJob>();
        services.AddScoped<INotificationService, NotificationService>();

        services.AddScoped<NotificationInboxService>();
        services.AddScoped<INotificationInboxService>(sp => sp.GetRequiredService<NotificationInboxService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<NotificationInboxService>());

        services.AddScoped<INotificationMeterProvider, NotificationMeterProvider>();
        services.AddScoped<IGoogleAdminService, GoogleAdminService>();

        // Shift management services
        services.AddScoped<IShiftManagementService, ShiftManagementService>();

        services.AddScoped<ShiftSignupService>();
        services.AddScoped<IShiftSignupService>(sp => sp.GetRequiredService<ShiftSignupService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ShiftSignupService>());

        services.AddScoped<IGeneralAvailabilityService, GeneralAvailabilityService>();

        services.AddScoped<ICalendarService, CalendarService>();

        // Feedback API key
        services.Configure<FeedbackApiSettings>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("FEEDBACK_API_KEY") ?? string.Empty;
        });
        services.AddScoped<ApiKeyAuthFilter>();

        // Log API key (separate credential from feedback)
        services.Configure<LogApiSettings>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("LOG_API_KEY") ?? string.Empty;
        });
        services.AddScoped<LogApiKeyAuthFilter>();

        // Ticket vendor integration
        var ticketVendorApiKey = Environment.GetEnvironmentVariable("TICKET_VENDOR_API_KEY") ?? string.Empty;

        services.Configure<TicketVendorSettings>(opts =>
        {
            configuration.GetSection(TicketVendorSettings.SectionName).Bind(opts);
            opts.ApiKey = ticketVendorApiKey;
        });

        if (environment.IsProduction())
        {
            services.AddHttpClient<ITicketVendorService, TicketTailorService>();
        }
        else
        {
            // Stub is self-contained — fill in defaults so IsConfigured passes
            services.PostConfigure<TicketVendorSettings>(opts =>
            {
                if (string.IsNullOrEmpty(opts.EventId)) opts.EventId = "stub-event";
                if (string.IsNullOrEmpty(opts.ApiKey)) opts.ApiKey = "stub";
            });
            services.AddScoped<ITicketVendorService, StubTicketVendorService>();
        }

        services.AddScoped<ITicketSyncService, TicketSyncService>();

        services.AddScoped<TicketQueryService>();
        services.AddScoped<ITicketQueryService>(sp => sp.GetRequiredService<TicketQueryService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<TicketQueryService>());

        // GDPR export orchestrator — pure fan-out over every IUserDataContributor
        services.AddScoped<IGdprExportService, GdprExportService>();

        // Stripe integration (read-only — fee tracking and payment method attribution)
        services.Configure<StripeSettings>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("STRIPE_API_KEY") ?? string.Empty;
        });
        services.AddScoped<IStripeService, StripeService>();

        return services;
    }
}
