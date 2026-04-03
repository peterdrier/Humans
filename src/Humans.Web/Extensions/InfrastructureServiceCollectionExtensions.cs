using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Humans.Web.Filters;

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

        services.AddSingleton<HumansMetricsService>();
        services.AddSingleton<IHumansMetrics>(sp => sp.GetRequiredService<HumansMetricsService>());

        services.AddScoped<ITeamService, TeamService>();
        services.AddScoped<ITeamPageService, TeamPageService>();
        services.AddScoped<ICampService, CampService>();
        services.AddScoped<ICampContactService, CampContactService>();
        services.AddScoped<ICommunicationPreferenceService, CommunicationPreferenceService>();
        services.AddScoped<ICampaignService, CampaignService>();
        services.AddScoped<IContactFieldService, ContactFieldService>();
        services.AddScoped<IUserEmailService, UserEmailService>();
        services.AddScoped<IEmailProvisioningService, EmailProvisioningService>();
        services.AddScoped<VolunteerHistoryService>();
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
        services.AddScoped<IMembershipCalculator, MembershipCalculator>();
        services.AddScoped<IRoleAssignmentService, RoleAssignmentService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IAccountMergeService, AccountMergeService>();
        services.AddScoped<IDuplicateAccountService, DuplicateAccountService>();
        services.AddScoped<IContactService, ContactService>();
        services.AddScoped<IMagicLinkService, MagicLinkService>();
        services.AddScoped<IFeedbackService, FeedbackService>();
        services.AddScoped<IBudgetService, BudgetService>();
        services.AddScoped<ITicketingBudgetService, TicketingBudgetService>();
        services.AddScoped<IApplicationDecisionService, ApplicationDecisionService>();
        services.AddScoped<IOnboardingService, OnboardingService>();
        services.AddScoped<IConsentService, ConsentService>();
        services.AddScoped<IProfileService, ProfileService>();
        services.AddScoped<SystemTeamSyncJob>();
        services.AddScoped<ISystemTeamSync>(sp => sp.GetRequiredService<SystemTeamSyncJob>());
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
        services.AddScoped<INotificationInboxService, NotificationInboxService>();
        services.AddScoped<INotificationMeterProvider, NotificationMeterProvider>();
        services.AddScoped<IGoogleAdminService, GoogleAdminService>();

        // Shift management services
        services.AddScoped<IShiftManagementService, ShiftManagementService>();
        services.AddScoped<IShiftSignupService, ShiftSignupService>();
        services.AddScoped<IGeneralAvailabilityService, GeneralAvailabilityService>();

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
        services.Configure<TicketVendorSettings>(opts =>
        {
            configuration.GetSection(TicketVendorSettings.SectionName).Bind(opts);
            // Populate API key from environment variable (not in appsettings — sensitive)
            opts.ApiKey = Environment.GetEnvironmentVariable("TICKET_VENDOR_API_KEY") ?? string.Empty;
        });
        services.AddHttpClient<ITicketVendorService, TicketTailorService>();
        services.AddScoped<ITicketSyncService, TicketSyncService>();
        services.AddScoped<ITicketQueryService, TicketQueryService>();

        // Stripe integration (read-only — fee tracking and payment method attribution)
        services.Configure<StripeSettings>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("STRIPE_API_KEY") ?? string.Empty;
        });
        services.AddScoped<IStripeService, StripeService>();

        return services;
    }
}
