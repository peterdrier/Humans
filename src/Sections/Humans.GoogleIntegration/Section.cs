using Humans.Gdpr.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Jobs;
using Humans.GoogleIntegration.Services;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.Base.Configuration;
using Humans.Base.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Humans.Base.Interfaces;

namespace Humans.GoogleIntegration;

/// <summary>
/// GoogleIntegration's DI entry point, at the project root by convention. Discovered by
/// Shell — nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// <para>
/// Two Shell files collapsed into this one: <c>GoogleIntegrationSectionExtensions</c> (the
/// repositories and the four services that need no Google credentials) and
/// <c>GoogleWorkspaceInfrastructureExtensions</c> (the connector graph).
/// </para>
/// <para>
/// <c>Configure&lt;GoogleWorkspaceSettings&gt;</c> and its "Production must have Google
/// credentials" guard moved in here too (nobodies-collective/Humans#1091): once
/// <c>GoogleWorkspaceHealthCheck</c> followed the connectors into this section, the settings
/// had no reader left outside it. <c>Configure&lt;GoogleWorkspaceOptions&gt;</c> stays in
/// Shell's <c>InfrastructureServiceCollectionExtensions</c> — Camps' <c>CampRoleService</c>
/// and Users' <c>ProfileController</c> still read it directly (Governance's rule: the section
/// that owns the file is not always the section that owns the line). <see
/// cref="ISection.Register"/> is handed no <c>IHostEnvironment</c>, so the guard reads
/// <c>HostDefaults.EnvironmentKey</c> off the configuration it does get and fails closed —
/// the same mechanism Development's section uses.
/// </para>
/// <para>
/// The two recurring jobs live in this project's <c>Contracts/</c> folder; their registration
/// and schedule are contributed via <c>SectionJobs.cs</c> (#1074's jobs seam).
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<GoogleWorkspaceSettings>(configuration.GetSection(GoogleWorkspaceSettings.SectionName));

        var googleWorkspaceConfig = configuration.GetSection(GoogleWorkspaceSettings.SectionName);
        var hasGoogleCredentials = !string.IsNullOrEmpty(googleWorkspaceConfig["ServiceAccountKeyPath"]) ||
                                   !string.IsNullOrEmpty(googleWorkspaceConfig["ServiceAccountKeyJson"]);

        var environmentName = configuration[HostDefaults.EnvironmentKey];
        if (!hasGoogleCredentials
            && string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Google Workspace credentials are required in production. " +
                "Set GoogleWorkspace:ServiceAccountKeyPath or GoogleWorkspace:ServiceAccountKeyJson.");
        }

        services.AddSectionDbContext<GoogleIntegrationDbContext>(sentinelTable: "google_resources");

        services.AddSingleton<ISyncSettingsRepository, SyncSettingsRepository>();
        services.AddSingleton<IGoogleResourceRepository, GoogleResourceRepository>();
        services.AddSingleton<IGoogleSyncOutboxRepository, GoogleSyncOutboxRepository>();
        services.AddSingleton<IGoogleSyncLogRepository, GoogleSyncLogRepository>();

        services.AddScoped<GoogleSyncLogService>();
        services.AddScoped<IGoogleSyncLogService>(sp => sp.GetRequiredService<GoogleSyncLogService>());
        services.AddScoped<IGoogleSyncLogViewer>(sp => sp.GetRequiredService<GoogleSyncLogService>());
        // google_sync_log holds per-user rows → GDPR export contributor (design-rules §8a).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<GoogleSyncLogService>());
        // Temporary: backs /Google/Admin/SyncHistoryMigration, and comes out with the six
        // Google columns on audit_log (nobodies-collective/Humans#1083).
        services.AddScoped<IGoogleSyncHistoryMigrationService, GoogleSyncHistoryMigrationService>();
        services.AddScoped<ISyncSettingsService, SyncSettingsService>();
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

        services.AddScoped<ITeamResourceService, TeamResourceService>();

        // Real Google clients when a service-account key is configured, stubs otherwise. The
        // "otherwise" arm is unreachable in Production — the guard above throws before this
        // runs.
        if (hasGoogleCredentials)
        {
            services.AddScoped<IGoogleSyncService, GoogleWorkspaceSyncService>();
            services.AddScoped<IGoogleSyncServiceRead>(sp => sp.GetRequiredService<IGoogleSyncService>());
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
            services.AddScoped<IGoogleSyncService, StubGoogleSyncService>();
            services.AddScoped<IGoogleSyncServiceRead>(sp => sp.GetRequiredService<IGoogleSyncService>());
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
        services.AddScoped<IGoogleTranslationService, GoogleTranslationService>();

        services.AddScoped<GoogleResourceReconciliationJob>();
        services.AddScoped<ProcessGoogleSyncOutboxJob>();

        // Gauge-refresh loop split out of HumansMetricsService (nobodies-collective/Humans#1091).
        services.AddSingleton<GoogleIntegrationMetricsService>();
        services.AddHostedService(sp => sp.GetRequiredService<GoogleIntegrationMetricsService>());
    }
}
