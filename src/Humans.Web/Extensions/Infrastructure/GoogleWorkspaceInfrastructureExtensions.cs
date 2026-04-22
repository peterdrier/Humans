using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Humans.Infrastructure.Services.GoogleWorkspace;
using GoogleWorkspaceUserService = Humans.Application.Services.GoogleIntegration.GoogleWorkspaceUserService;

namespace Humans.Web.Extensions.Infrastructure;

internal static class GoogleWorkspaceInfrastructureExtensions
{
    internal static IServiceCollection AddGoogleWorkspaceInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.Configure<GoogleWorkspaceSettings>(configuration.GetSection(GoogleWorkspaceSettings.SectionName));
        services.Configure<TeamResourceManagementSettings>(configuration.GetSection(TeamResourceManagementSettings.SectionName));

        var googleWorkspaceConfig = configuration.GetSection(GoogleWorkspaceSettings.SectionName);
        var hasGoogleCredentials = !string.IsNullOrEmpty(googleWorkspaceConfig["ServiceAccountKeyPath"]) ||
                                   !string.IsNullOrEmpty(googleWorkspaceConfig["ServiceAccountKeyJson"]);

        if (hasGoogleCredentials)
        {
            services.AddScoped<IGoogleSyncService, GoogleWorkspaceSyncService>();
            services.AddScoped<ITeamResourceService, TeamResourceService>();
            services.AddScoped<IDriveActivityMonitorService, DriveActivityMonitorService>();

            // Google Integration §15 migration (issue #554) — workspace users.
            // Application-layer service depends only on the shape-neutral
            // IWorkspaceUserDirectoryClient connector; the real and stub
            // implementations live in Humans.Infrastructure.
            services.AddScoped<IWorkspaceUserDirectoryClient, WorkspaceUserDirectoryClient>();
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

            services.AddScoped<IWorkspaceUserDirectoryClient, StubWorkspaceUserDirectoryClient>();
            services.AddScoped<IGoogleWorkspaceUserService, GoogleWorkspaceUserService>();
        }

        services.AddScoped<IGoogleAdminService, GoogleAdminService>();

        services.AddScoped<GoogleResourceReconciliationJob>();
        services.AddScoped<DriveActivityMonitorJob>();
        services.AddScoped<ProcessGoogleSyncOutboxJob>();

        return services;
    }
}
