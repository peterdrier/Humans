using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Repositories;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Services;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Application.Interfaces;

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
/// Three lines did **not** come with them, because the section owns the file but not the
/// line (Governance's rule). <c>Configure&lt;GoogleWorkspaceSettings&gt;</c> and
/// <c>Configure&lt;GoogleWorkspaceOptions&gt;</c> bind Base-owned types that Base and Shell
/// read too — <c>CampRoleService</c> and <c>ProfileController</c> take the options,
/// <c>GoogleWorkspaceHealthCheck</c> takes the settings — and the
/// "Production must have Google credentials" startup guard beside them needs an
/// <c>IHostEnvironment</c>, which <see cref="ISection.Register"/> is not handed. All three
/// stayed in Shell's <c>InfrastructureServiceCollectionExtensions</c>. The half that is
/// genuinely the section's — which of its two connector sets to bind — reads the same
/// configuration keys the guard does (Email's split, G5-SECTION-TEMPLATE.md step 4).
/// </para>
/// <para>
/// The recurring jobs stay in <c>Humans.Infrastructure/Jobs</c> and are registered from Shell
/// with the rest of the roll-call: <c>UseHumansRecurringJobs</c> names them by concrete type
/// and there is no <c>ISection</c>-style discovery seam for jobs yet (step 6b).
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<GoogleIntegrationDbContext>(sentinelTable: "google_resources");

        services.AddSingleton<ISyncSettingsRepository, SyncSettingsRepository>();
        services.AddSingleton<IGoogleResourceRepository, GoogleResourceRepository>();
        services.AddSingleton<IGoogleSyncOutboxRepository, GoogleSyncOutboxRepository>();

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
        // "otherwise" arm is unreachable in Production — Shell throws at startup before this
        // runs (see the remarks above).
        var googleWorkspaceConfig = configuration.GetSection("GoogleWorkspace");
        var hasGoogleCredentials = !string.IsNullOrEmpty(googleWorkspaceConfig["ServiceAccountKeyPath"]) ||
                                   !string.IsNullOrEmpty(googleWorkspaceConfig["ServiceAccountKeyJson"]);

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
    }
}
