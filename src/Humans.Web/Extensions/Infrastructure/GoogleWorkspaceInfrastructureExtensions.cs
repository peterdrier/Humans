using Humans.Application.Configuration;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Jobs;

namespace Humans.Web.Extensions.Infrastructure;

/// <summary>
/// The three Google Workspace lines that did <em>not</em> move into
/// <c>Humans.GoogleIntegration</c>'s <c>Section.Register</c> at that section's G5
/// (nobodies-collective/Humans#866).
/// </summary>
/// <remarks>
/// Both settings types are Base's and are read from outside the section — <c>CampRoleService</c>
/// and <c>ProfileController</c> take <see cref="GoogleWorkspaceOptions"/>,
/// <c>GoogleWorkspaceHealthCheck</c> takes <see cref="GoogleWorkspaceSettings"/> — so the
/// section that owns the connectors does not own these bindings (Governance's rule). The
/// credentials guard stays for a second reason: <c>ISection.Register</c> is handed no
/// <c>IHostEnvironment</c>, and pushing the check into a service factory would first throw on
/// a real Google call instead of at boot (Email's split, G5-SECTION-TEMPLATE.md step 4). The
/// section reads the same two configuration keys to decide which of its own connector sets to
/// bind.
/// <para>
/// The three recurring jobs stay in <c>Humans.Infrastructure/Jobs</c> because
/// <c>UseHumansRecurringJobs</c> names them by concrete type (step 6b); each reaches the
/// section through <c>Humans.GoogleIntegration.Contracts</c>.
/// </para>
/// </remarks>
internal static class GoogleWorkspaceInfrastructureExtensions
{
    internal static IServiceCollection AddGoogleWorkspaceInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.Configure<GoogleWorkspaceSettings>(configuration.GetSection(GoogleWorkspaceSettings.SectionName));
        services.Configure<GoogleWorkspaceOptions>(configuration.GetSection(GoogleWorkspaceOptions.SectionName));

        var googleWorkspaceConfig = configuration.GetSection(GoogleWorkspaceSettings.SectionName);
        var hasGoogleCredentials = !string.IsNullOrEmpty(googleWorkspaceConfig["ServiceAccountKeyPath"]) ||
                                   !string.IsNullOrEmpty(googleWorkspaceConfig["ServiceAccountKeyJson"]);

        if (!hasGoogleCredentials && environment.IsProduction())
        {
            throw new InvalidOperationException(
                "Google Workspace credentials are required in production. " +
                "Set GoogleWorkspace:ServiceAccountKeyPath or GoogleWorkspace:ServiceAccountKeyJson.");
        }

        services.AddScoped<GoogleResourceReconciliationJob>();
        services.AddScoped<DriveActivityMonitorJob>();
        services.AddScoped<ProcessGoogleSyncOutboxJob>();

        return services;
    }
}
