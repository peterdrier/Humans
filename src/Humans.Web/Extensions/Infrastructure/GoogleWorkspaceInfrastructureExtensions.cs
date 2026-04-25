using Humans.Application.Configuration;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Teams;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Humans.Infrastructure.Services.GoogleWorkspace;
using GoogleWorkspaceUserService = Humans.Application.Services.GoogleIntegration.GoogleWorkspaceUserService;
using GoogleDriveActivityMonitorService = Humans.Application.Services.GoogleIntegration.DriveActivityMonitorService;
using GoogleAdminService = Humans.Application.Services.GoogleIntegration.GoogleAdminService;
using GoogleWorkspaceSyncService = Humans.Application.Services.GoogleIntegration.GoogleWorkspaceSyncService;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Teams;
using Humans.Infrastructure.Repositories.GoogleIntegration;

namespace Humans.Web.Extensions.Infrastructure;

internal static class GoogleWorkspaceInfrastructureExtensions
{
    internal static IServiceCollection AddGoogleWorkspaceInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.Configure<GoogleWorkspaceSettings>(configuration.GetSection(GoogleWorkspaceSettings.SectionName));
        // §15 Part 2b — Application-layer sync service reads non-sensitive
        // fields (Domain, CustomerId, TeamFoldersParentId, Groups) through
        // this Application-owned options type. Same appsettings section as
        // GoogleWorkspaceSettings so a single config surface drives both.
        services.Configure<GoogleWorkspaceOptions>(configuration.GetSection(GoogleWorkspaceOptions.SectionName));
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

        // Google sync outbox repository (issue #554 Part 1) — exposes narrow
        // count queries so Notifications / Metrics / Admin-digest consumers
        // don't read google_sync_outbox_events directly. Singleton via
        // IDbContextFactory per §15b.
        services.AddSingleton<IGoogleSyncOutboxRepository, GoogleSyncOutboxRepository>();

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

            // §15 Part 2a (issue #574) — SDK bridge interfaces for the
            // GoogleWorkspaceSyncService migration in Part 2b (#575). These
            // are registered alongside the existing SDK-direct service
            // (dual-path during 2a → 2b transition). GoogleWorkspaceSyncService
            // continues to import Google.Apis.* directly until Part 2b strips
            // it; no Application-layer imports are added here.
            services.AddScoped<IGoogleGroupMembershipClient, GoogleGroupMembershipClient>();
            services.AddScoped<IGoogleGroupProvisioningClient, GoogleGroupProvisioningClient>();
            services.AddScoped<IGoogleDrivePermissionsClient, GoogleDrivePermissionsClient>();
            services.AddScoped<IGoogleDirectoryClient, GoogleDirectoryClient>();
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

            // §15 Part 2a (issue #574) — stub SDK bridge interfaces for dev
            // environments without Google credentials. Registered as
            // Singletons so each stub's in-memory state survives across
            // scoped requests (matching the once-per-process behaviour of
            // the real SDK client handles).
            services.AddSingleton<IGoogleGroupMembershipClient, StubGoogleGroupMembershipClient>();
            services.AddSingleton<IGoogleGroupProvisioningClient, StubGoogleGroupProvisioningClient>();
            services.AddSingleton<IGoogleDrivePermissionsClient, StubGoogleDrivePermissionsClient>();
            services.AddSingleton<IGoogleDirectoryClient, StubGoogleDirectoryClient>();
        }

        services.AddScoped<IGoogleAdminService, GoogleAdminService>();

        services.AddScoped<GoogleResourceReconciliationJob>();
        services.AddScoped<DriveActivityMonitorJob>();
        services.AddScoped<ProcessGoogleSyncOutboxJob>();

        return services;
    }
}
