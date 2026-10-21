using Humans.Email.Contracts;
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
using Humans.Base.Interfaces;
using Humans.Users.Contracts;

namespace Humans.GoogleIntegration;

/// <summary>
/// GoogleIntegration's DI entry point, at the project root by convention. Discovered by
/// Shell — nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// <para>
/// <c>Configure&lt;GoogleWorkspaceSettings&gt;</c> is configured here; nothing outside this
/// section reads it. <c>Configure&lt;GoogleWorkspaceOptions&gt;</c> is configured in
/// Shell's <c>GoogleWorkspaceInfrastructureExtensions</c> — Camps' <c>CampRoleService</c>
/// and Users' <c>ProfileEmailsController</c> still read it directly (Governance's rule: the section
/// that owns the file is not always the section that owns the line).
/// </para>
/// <para>
/// The two recurring jobs live in this project's <c>Jobs/</c> folder; their registration
/// and schedule are contributed via <c>SectionJobs.cs</c>.
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

        services.AddSectionDbContext<GoogleIntegrationDbContext>(sentinelTable: "google_resources");

        services.AddSingleton<ISyncSettingsRepository, SyncSettingsRepository>();
        services.AddSingleton<IGoogleResourceRepository, GoogleResourceRepository>();
        services.AddSingleton<IGoogleSyncOutboxRepository, GoogleSyncOutboxRepository>();
        services.AddSingleton<IGoogleSyncLogRepository, GoogleSyncLogRepository>();
        services.AddSingleton<IWorkspaceIdentityRepository, WorkspaceIdentityRepository>();

        // google_workspace_identities (Users decomposition commit 15): reads, the Users seam,
        // the Article 15/17 slice and the account-merge fold.
        services.AddScoped<WorkspaceIdentityService>();
        services.AddScoped<IWorkspaceIdentityManagement>(sp => sp.GetRequiredService<WorkspaceIdentityService>());
        services.AddScoped<IGoogleIntegrationService>(sp => sp.GetRequiredService<WorkspaceIdentityService>());
        services.AddScoped<IGoogleIntegrationWrite>(sp => sp.GetRequiredService<WorkspaceIdentityService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<WorkspaceIdentityService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<WorkspaceIdentityService>());

        services.AddScoped<GoogleSyncLogService>();
        services.AddScoped<IGoogleSyncLogService>(sp => sp.GetRequiredService<GoogleSyncLogService>());
        services.AddScoped<IGoogleSyncLogViewer>(sp => sp.GetRequiredService<GoogleSyncLogService>());
        // google_sync_log holds per-user rows → GDPR export contributor (design-rules §8a).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<GoogleSyncLogService>());
        // Temporary: backs /Google/Admin/SyncHistoryMigration, and comes out with the six
        // Google columns on audit_log (nobodies-collective/Humans#1083).
        services.AddScoped<IGoogleSyncHistoryMigrationService, GoogleSyncHistoryMigrationService>();
        services.AddScoped<ISyncSettingsService, SyncSettingsService>();
        // GoogleIntegration owns its email copy and its gallery samples; Email keeps the
        // mechanics (memory/architecture/email-templates-live-in-sender.md).
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
        services.AddScoped<ITeamResourceWrite>(sp => sp.GetRequiredService<ITeamResourceManagement>());

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
}
