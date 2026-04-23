using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Teams;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Services;
using Humans.Infrastructure.Services.GoogleWorkspace;
using GoogleWorkspaceUserService = Humans.Application.Services.GoogleIntegration.GoogleWorkspaceUserService;
using GoogleDriveActivityMonitorService = Humans.Application.Services.GoogleIntegration.DriveActivityMonitorService;
using GoogleAdminService = Humans.Application.Services.GoogleIntegration.GoogleAdminService;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Teams;

namespace Humans.Web.Extensions.Infrastructure;

internal static class GoogleWorkspaceInfrastructureExtensions
{
    internal static IServiceCollection AddGoogleWorkspaceInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.Configure<GoogleWorkspaceSettings>(configuration.GetSection(GoogleWorkspaceSettings.SectionName));
        services.AddSingleton(sp =>
        {
            var opts = new TeamResourceManagementOptions();
            configuration.GetSection(TeamResourceManagementOptions.SectionName).Bind(opts);
            return opts;
        });

        var googleWorkspaceConfig = configuration.GetSection(GoogleWorkspaceSettings.SectionName);
        var hasGoogleCredentials = !string.IsNullOrEmpty(googleWorkspaceConfig["ServiceAccountKeyPath"]) ||
                                   !string.IsNullOrEmpty(googleWorkspaceConfig["ServiceAccountKeyJson"]);

        // Team-resource repository is Singleton (IDbContextFactory-based per §15b).
        services.AddSingleton<IGoogleResourceRepository, GoogleResourceRepository>();

        // Application-layer service uses the same repo + one of the two Google
        // connector implementations below. Stub connector is used when no
        // service-account credentials are configured (dev only).
        services.AddScoped<ITeamResourceService, TeamResourceService>();

        // Google Integration §15 migration (issue #554) — Drive activity monitor.
        // Repository is Singleton (IDbContextFactory-based); the service lives in
        // Humans.Application and depends only on IGoogleDriveActivityClient and
        // the repository, so it stays free of Google SDK / EF imports. The
        // connector client has real and stub implementations.
        services.AddSingleton<IDriveActivityMonitorRepository, DriveActivityMonitorRepository>();
        services.AddScoped<IDriveActivityMonitorService, GoogleDriveActivityMonitorService>();

        if (hasGoogleCredentials)
        {
            services.AddScoped<IGoogleSyncService, GoogleWorkspaceSyncService>();
            services.AddScoped<ITeamResourceGoogleClient, TeamResourceGoogleClient>();
            services.AddScoped<IGoogleDriveActivityClient, GoogleDriveActivityClient>();

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
            services.AddScoped<ITeamResourceGoogleClient, StubTeamResourceGoogleClient>();
            services.AddScoped<IGoogleDriveActivityClient, StubGoogleDriveActivityClient>();

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
