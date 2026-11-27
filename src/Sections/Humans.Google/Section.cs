using Humans.Base.Demo;
using Humans.Google.Domain;
using Humans.Teams.Contracts;
using Humans.Base.Models.Tables;
using Humans.Base.Authorization;
using Humans.Base.Configuration;
using Humans.Base.Constants;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Google.Authorization;
using Humans.Google.Contracts;
using Humans.Google.Health;
using Humans.Google.Jobs;
using Humans.Google.Services;
using Humans.Google.Services.Workspace;
using Humans.Google.ViewComponents;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Humans.Notifications.Contracts;
using Humans.Permissions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Humans.AuditLog.Contracts;
using Humans.Base.Models;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;
using Humans.Google.Controllers;

[assembly: NoDemoStory("Mirrors Google Workspace: demo people must never reach real Google.")]

namespace Humans.Google;

public sealed class Section :
    SectionBase,
    ISectionAccessMatrix,
    ISectionHelp,
    ISectionAdminNav,
    ISectionConfiguration,
    ISectionHealthChecks,
    ISectionJobs,
    ISectionPermissions,
    ISectionSettings,
    IUserPart
{
    IReadOnlyList<AccessMatrixData> ISectionAccessMatrix.AccessMatrices =>
    [
        new()
        {
            Key = "GoogleSync",
            SectionName = "Google Sync",
            Order = 93,
            Roles = ["Admin"],
            Features =
            [
                Of("Sync settings", ("Admin", Allowed)),
            ]
        }
    ];

    IReadOnlyList<SectionHelpEntry> ISectionHelp.HelpEntries => HelpEntries;

    // Read once per process: the markdown ships inside this assembly and never changes at runtime.
    private static readonly IReadOnlyList<SectionHelpEntry> HelpEntries =
        [SectionHelpEntry.FromEmbeddedMarkdown(typeof(Section).Assembly, "GoogleSync", 84)];

    public override void Register(IServiceCollection services, IConfiguration configuration)
    {
        EnumBadgeMap.Register(new Dictionary<Enum, string>
        {
            [GoogleEmailStatus.Unknown] = "bg-secondary",
            [GoogleEmailStatus.Valid] = "bg-success",
            [GoogleEmailStatus.Rejected] = "bg-danger",

            [SyncMode.None] = "bg-secondary",
            [SyncMode.AddOnly] = "bg-info",
            [SyncMode.AddAndRemove] = "bg-success",
        });

        services.AddSingleton<IAuditActionContributor, GoogleAuditActions>();
        services.AddSingleton<INotificationSourceContributor, GoogleNotificationSources>();
        services.AddScoped<INotificationMeterContributor, GoogleNotificationMeters>();
        services.Configure<GoogleWorkspaceSettings>(configuration.GetSection(GoogleWorkspaceSettings.SectionName));
        services.Configure<GoogleWorkspaceOptions>(configuration.GetSection(GoogleWorkspaceOptions.SectionName));

        var googleWorkspaceConfig = configuration.GetSection(GoogleWorkspaceSettings.SectionName);
        var hasGoogleCredentials = !string.IsNullOrEmpty(googleWorkspaceConfig["ServiceAccountKeyPath"]) ||
                                   !string.IsNullOrEmpty(googleWorkspaceConfig["ServiceAccountKeyJson"]);

        services.AddSectionDbContext<GoogleIntegrationDbContext>(sentinelTable: "google_resources");

        services.AddSingleton<ISyncSettingsRepository, SyncSettingsRepository>();
        services.AddSingleton<IGoogleResourceRepository, GoogleResourceRepository>();
        services.AddSingleton<IGoogleSyncOutboxRepository, GoogleSyncOutboxRepository>();
        services.AddSingleton<IGoogleSyncLogRepository, GoogleSyncLogRepository>();
        services.AddSingleton<IWorkspaceIdentityRepository, WorkspaceIdentityRepository>();

        services.AddScoped<WorkspaceIdentityService>();
        services.AddScoped<IWorkspaceIdentityManagement>(sp => sp.GetRequiredService<WorkspaceIdentityService>());
        services.AddScoped<IGoogleService>(sp => sp.GetRequiredService<WorkspaceIdentityService>());
        services.AddScoped<IUserEmailRemovalListener>(sp => sp.GetRequiredService<WorkspaceIdentityService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<WorkspaceIdentityService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<WorkspaceIdentityService>());

        services.AddScoped<GoogleSyncLogService>();
        services.AddScoped<IGoogleSyncLogService>(sp => sp.GetRequiredService<GoogleSyncLogService>());
        services.AddScoped<IGoogleSyncLogViewer>(sp => sp.GetRequiredService<GoogleSyncLogService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<GoogleSyncLogService>());
        services.AddScoped<IRetentionPeriodContributor>(sp => sp.GetRequiredService<GoogleSyncLogService>());
        // Temporary: backs /Google/Admin/SyncHistoryMigration; remove with the six Google columns on audit_log.
        services.AddScoped<IGoogleSyncHistoryMigrationService, GoogleSyncHistoryMigrationService>();
        services.AddKeyedScoped<ISyncSettingsService, SyncSettingsService>(CachingSyncSettingsService.InnerServiceKey);
        services.AddSingleton<CachingSyncSettingsService>();
        services.AddSingleton<ISyncSettingsService>(sp => sp.GetRequiredService<CachingSyncSettingsService>());
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingSyncSettingsService>());
        services.AddHostedService(sp => sp.GetRequiredService<CachingSyncSettingsService>());
        // This section owns its email copy and gallery samples; Email keeps the mechanics.
        services.AddScoped<GoogleEmails>();
        services.AddScoped<IEmailPreviewContributor, GoogleEmailPreviews>();

        services.AddScoped<IEmailProvisioningService, EmailProvisioningService>();
        services.AddScoped<IGoogleSyncOutboxService, GoogleSyncOutboxService>();
        services.AddScoped<IGoogleAdminService, GoogleAdminService>();
        services.AddScoped<IGoogleRemovalNotificationService, GoogleRemovalNotificationService>();

        services.AddSingleton(_ =>
        {
            var opts = new TeamResourceManagementOptions();
            configuration.GetSection(TeamResourceManagementOptions.SectionName).Bind(opts);
            return opts;
        });

        services.AddScoped<ITeamResourceManagement, TeamResourceService>();
        services.AddScoped<ITeamResourceService>(sp => sp.GetRequiredService<ITeamResourceManagement>());
        services.AddScoped<IAuthorizationHandler, TeamResourceAuthorizationHandler>();

        // Real Google clients when a service-account key is configured, stubs otherwise.
        // The health check reports the missing configuration as Degraded in every environment.
        if (hasGoogleCredentials)
        {
            services.AddScoped<IGoogleSyncManagement, GoogleWorkspaceSyncService>();
            services.AddScoped<IGoogleDriveSync, GoogleDriveAccessSyncService>();
            services.AddScoped<IGoogleSyncService>(sp => sp.GetRequiredService<IGoogleSyncManagement>());
            services.AddScoped<IGoogleSyncWrite>(sp => sp.GetRequiredService<IGoogleSyncManagement>());
            services.AddScoped<ITeamResourceGoogleClient, TeamResourceGoogleClient>();
            services.AddScoped<IGoogleDriveActivityClient, GoogleDriveActivityClient>();

            services.AddScoped<IWorkspaceUserDirectoryClient, WorkspaceUserDirectoryClient>();
            services.AddScoped<IGoogleWorkspaceUserService, GoogleWorkspaceUserService>();

            services.AddScoped<IGoogleGroupMembershipClient, GoogleGroupMembershipClient>();
            services.AddScoped<IGoogleGroupProvisioningClient, GoogleGroupProvisioningClient>();
            services.AddScoped<IGoogleDrivePermissionsClient, GoogleDrivePermissionsClient>();
            services.AddScoped<IGoogleDirectoryClient, GoogleDirectoryClient>();

            services.AddHttpClient<GoogleTranslationClient>();
            services.AddScoped<IGoogleTranslationService>(sp => sp.GetRequiredService<GoogleTranslationClient>());

            services.AddDemoSafetyCheck<GoogleDemoSafetyCheck>(configuration);
        }
        else
        {
            services.AddScoped<IGoogleSyncManagement, StubGoogleSyncService>();
            services.AddScoped<IGoogleDriveSync>(sp => (StubGoogleSyncService)sp.GetRequiredService<IGoogleSyncManagement>());
            services.AddScoped<IGoogleSyncService>(sp => sp.GetRequiredService<IGoogleSyncManagement>());
            services.AddScoped<IGoogleSyncWrite>(sp => sp.GetRequiredService<IGoogleSyncManagement>());
            services.AddScoped<ITeamResourceGoogleClient, StubTeamResourceGoogleClient>();
            services.AddScoped<IGoogleDriveActivityClient, StubGoogleDriveActivityClient>();

            services.AddScoped<IWorkspaceUserDirectoryClient, StubWorkspaceUserDirectoryClient>();
            services.AddScoped<IGoogleWorkspaceUserService, GoogleWorkspaceUserService>();

            services.AddSingleton<IGoogleGroupMembershipClient, StubGoogleGroupMembershipClient>();
            services.AddSingleton<IGoogleGroupProvisioningClient, StubGoogleGroupProvisioningClient>();
            services.AddSingleton<IGoogleDrivePermissionsClient, StubGoogleDrivePermissionsClient>();
            services.AddSingleton<IGoogleDirectoryClient, StubGoogleDirectoryClient>();
            services.AddSingleton<StubGoogleTranslationClient>();
            services.AddScoped<IGoogleTranslationService>(sp => sp.GetRequiredService<StubGoogleTranslationClient>());
        }

        services.AddScoped<IGoogleSyncOutboxProcessor, GoogleSyncOutboxProcessor>();
        services.AddScoped<IGoogleGroupSyncScheduler, HangfireGoogleGroupSyncScheduler>();
        services.AddScoped<IGoogleGroupSync, GoogleGroupSyncService>();
        services.AddScoped<IGoogleDriveAccessSyncScheduler, HangfireGoogleDriveAccessSyncScheduler>();

        services.AddScoped<GoogleResourceReconciliationJob>();
        services.AddScoped<ProcessGoogleSyncOutboxJob>();

        services.AddSingleton<GoogleMetricsService>();
        services.AddHostedService(sp => sp.GetRequiredService<GoogleMetricsService>());
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        AdminGroup([
            AdminLink<GoogleController>("Overview", nameof(GoogleController.Index), "fa-brands fa-google", PolicyNames.AdminOnly),
            AdminLink<GoogleController>("Resource sync", nameof(GoogleController.Sync), "fa-solid fa-arrows-rotate", TeamsPolicies.Read),
            AdminLink<GoogleController>("All domain groups", nameof(GoogleController.AllGroups), "fa-solid fa-globe", PolicyNames.AdminOnly),
            AdminLink<GoogleController>("Workspace accounts", nameof(GoogleController.Accounts), "fa-solid fa-at", PolicyNames.AdminOnly),
            AdminLink<GoogleController>("Sync outbox", nameof(GoogleController.SyncOutbox), "fa-solid fa-clock-rotate-left", PolicyNames.AdminOnly),
            AdminLink<GoogleController>("Sync results", nameof(GoogleController.SyncResults), "fa-solid fa-list-check", PolicyNames.AdminOnly),
            AdminLink<GoogleController>("Activity Log", nameof(GoogleController.ActivityLog), "fa-solid fa-scroll", PolicyNames.AdminOnly),
            AdminLink<GoogleController>("Group settings", nameof(GoogleController.GroupSettingsResults), "fa-solid fa-gears", PolicyNames.AdminOnly),
            AdminLink<GoogleController>("Email renames", nameof(GoogleController.EmailRenames), "fa-solid fa-right-left", PolicyNames.AdminOnly),
            AdminLink<GoogleController>("Email flag violations", nameof(GoogleController.EmailFlagViolations), "fa-solid fa-triangle-exclamation", PolicyNames.AdminOnly),
            AdminLink<GoogleSyncHistoryMigrationAdminController>("Sync history migration", nameof(GoogleSyncHistoryMigrationAdminController.Index), "fa-solid fa-arrow-right-arrow-left", PolicyNames.AdminOnly)
        ])
    ];

    void ISectionConfiguration.DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "GoogleWorkspace:ServiceAccountKeyPath", "Google Workspace",
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "GoogleWorkspace:ServiceAccountKeyJson", "Google Workspace",
            isSensitive: true, importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "GoogleWorkspace:Domain", "Google Workspace",
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "GoogleWorkspace:CustomerId", "Google Workspace",
            importance: ConfigurationImportance.Recommended);

        // Team Resource Management toggle. Grouped under Teams on the admin page because that
        // is the feature it gates, though this section is what reads it.
        configuration.GetOptionalSetting(registry, "TeamResourceManagement:AllowCoordinatorsToManageResources",
            "Teams");
    }

    void ISectionHealthChecks.AddHealthChecks(IHealthChecksBuilder builder, IConfiguration configuration)
    {
        builder.AddCheck<GoogleWorkspaceHealthCheck>("google-workspace", tags: [HealthCheckTags.External]);
    }

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "google-resource-reconciliation", typeof(GoogleResourceReconciliationJob), "0 3 * * *");

        yield return new RecurringJobDescriptor(
            "google-sync-outbox-process", typeof(ProcessGoogleSyncOutboxJob), "*/10 * * * *");
    }

    IEnumerable<SettingsTab> ISectionSettings.Tabs() =>
    [
        new SettingsTab("google-sync", "Settings_TabGoogleSync", typeof(GoogleSyncSettingsTabViewComponent), PolicyNames.AdminOnly)
    ];

    IEnumerable<UserPart> IUserPart.Parts() =>
        [new(UserPartSlots.AdminDetailSidebar, typeof(WorkspaceIdentityCardViewComponent), Weight: 110)];

    IReadOnlyList<Permission> ISectionPermissions.Permissions { get; } =
    [
        new(GooglePermissions.ManageResources, "Google_Permission_ManageResources_Title",
            "Google_Permission_ManageResources_Blurb", [TeamOwner.Kind], TopLevelOnly: true,
            AdminRoles: [TeamsRoles.TeamsAdmin], typeof(GoogleResource)),
    ];
}
