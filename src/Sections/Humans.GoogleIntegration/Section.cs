using Humans.Teams.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Configuration;
using Humans.Base.Constants;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.GoogleIntegration.Authorization;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Health;
using Humans.GoogleIntegration.Jobs;
using Humans.GoogleIntegration.Services;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.GoogleIntegration.ViewComponents;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.GoogleIntegration;

/// <summary><c>GoogleWorkspaceOptions</c> is bound in Shell, not here: Camps and Users read it too.</summary>
public sealed class Section :
    ISection,
    ISectionAdminNav,
    ISectionConfiguration,
    ISectionDataMigrations,
    ISectionHealthChecks,
    ISectionJobs,
    ISectionSettings,
    IUserPart
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<INotificationSourceContributor, GoogleIntegrationNotificationSources>();
        services.Configure<GoogleWorkspaceSettings>(configuration.GetSection(GoogleWorkspaceSettings.SectionName));

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
        services.AddScoped<IGoogleIntegrationService>(sp => sp.GetRequiredService<WorkspaceIdentityService>());
        services.AddScoped<IGoogleIntegrationWrite>(sp => sp.GetRequiredService<WorkspaceIdentityService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<WorkspaceIdentityService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<WorkspaceIdentityService>());

        services.AddScoped<GoogleSyncLogService>();
        services.AddScoped<IGoogleSyncLogService>(sp => sp.GetRequiredService<GoogleSyncLogService>());
        services.AddScoped<IGoogleSyncLogViewer>(sp => sp.GetRequiredService<GoogleSyncLogService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<GoogleSyncLogService>());
        // Temporary: backs /Google/Admin/SyncHistoryMigration; remove with the six Google columns on audit_log.
        services.AddScoped<IGoogleSyncHistoryMigrationService, GoogleSyncHistoryMigrationService>();
        services.AddScoped<ISyncSettingsService, SyncSettingsService>();
        // This section owns its email copy and gallery samples; Email keeps the mechanics.
        services.AddScoped<GoogleIntegrationEmails>();
        services.AddScoped<IEmailPreviewContributor, GoogleIntegrationEmailPreviews>();

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

            services.AddHttpClient<IGoogleTranslationClient, GoogleTranslationClient>();
        }
        else
        {
            services.AddScoped<IGoogleSyncManagement, StubGoogleSyncService>();
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
            services.AddSingleton<IGoogleTranslationClient, StubGoogleTranslationClient>();
        }

        services.AddScoped<IGoogleSyncOutboxProcessor, GoogleSyncOutboxProcessor>();
        services.AddScoped<IGoogleGroupSyncScheduler, HangfireGoogleGroupSyncScheduler>();
        services.AddScoped<IGoogleGroupSync, GoogleGroupSyncService>();
        services.AddScoped<IGoogleDriveAccessSyncScheduler, HangfireGoogleDriveAccessSyncScheduler>();
        services.AddScoped<IGoogleDriveSync, GoogleDriveAccessSyncService>();
        services.AddScoped<IGoogleTranslationService, GoogleTranslationService>();

        services.AddScoped<GoogleResourceReconciliationJob>();
        services.AddScoped<ProcessGoogleSyncOutboxJob>();

        services.AddSingleton<GoogleIntegrationMetricsService>();
        services.AddHostedService(sp => sp.GetRequiredService<GoogleIntegrationMetricsService>());
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Google", [
            new("Overview",              "Google", "Index",        null, null, "fa-brands fa-google",           PolicyNames.AdminOnly),
            new("Resource sync",         "Google", "Sync",         null, null, "fa-solid fa-arrows-rotate",     TeamsPolicies.Read),
            new("All domain groups",     "Google", "AllGroups",    null, null, "fa-solid fa-globe",             PolicyNames.AdminOnly),
            new("Workspace accounts",    "Google", "Accounts",     null, null, "fa-solid fa-at",                PolicyNames.AdminOnly),
            new("Sync outbox",           "Google", "SyncOutbox",   null, null, "fa-solid fa-clock-rotate-left", PolicyNames.AdminOnly),
            new("Sync results",          "Google", "SyncResults",  null, null, "fa-solid fa-list-check",        PolicyNames.AdminOnly),
            new("Activity Log",          "Google", "ActivityLog",  null, null, "fa-solid fa-scroll", PolicyNames.AdminOnly),
            new("Group settings",        "Google", "GroupSettingsResults", null, null, "fa-solid fa-gears",     PolicyNames.AdminOnly),
            new("Email renames",         "Google", "EmailRenames", null, null, "fa-solid fa-right-left",        PolicyNames.AdminOnly),
            new("Email flag violations", "Google", "EmailFlagViolations", null, null, "fa-solid fa-triangle-exclamation", PolicyNames.AdminOnly),
            new("Sync history migration", "GoogleSyncHistoryMigrationAdmin", "Index", null, null,
                "fa-solid fa-arrow-right-arrow-left", PolicyNames.AdminOnly)
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

    internal const string WorkspaceIdentitiesKey = "google.workspace-identities";

    IEnumerable<DataMigration> ISectionDataMigrations.Migrations() =>
    [
        new(WorkspaceIdentitiesKey, "GoogleIntegration",
            "Copy each member's Google identity (user_emails.IsGoogle and GoogleEmailStatus) into google_workspace_identities",
            "WorkspaceIdentityService's legacy fallback, UserEmailInfo.IsGoogle/GoogleEmailStatus and LegacyGoogleEmailStatus; then a drop PR for user_emails.IsGoogle and user_emails.GoogleEmailStatus",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Burner name", "Google identity", "Status"],
            [.. (await sp.GetRequiredService<IWorkspaceIdentityManagement>().GetUncopiedLegacyIdentitiesAsync(ct))
                .Select(r => (IReadOnlyList<string>)[r.UserId.ToString(), r.BurnerName, r.Email, r.Status.ToString()])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<IWorkspaceIdentityManagement>().CopyLegacyIdentitiesAsync(ct);

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
}
