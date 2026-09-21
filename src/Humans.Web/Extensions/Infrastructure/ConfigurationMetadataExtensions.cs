using Humans.Base.Configuration;
using Humans.Base.Interfaces;

namespace Humans.Web.Extensions.Infrastructure;

/// <summary>
/// Fills the <see cref="ConfigurationRegistry"/> the admin Configuration page reads. Shell
/// declares only the keys the host itself reads; every other key is declared by the section
/// that reads it, through <see cref="ISectionConfiguration"/>, and iterated here.
/// </summary>
internal static class ConfigurationMetadataExtensions
{
    internal static IServiceCollection AddConfigurationMetadata(
        this IServiceCollection services,
        IConfiguration configuration,
        ConfigurationRegistry? configRegistry)
    {
        if (configRegistry is not null)
        {
            // Host-owned. GitHub:* binds GitHubSettings in Shell's telemetry infrastructure and
            // four sections read it through that binding, so no one section owns the keys.
            configuration.GetRequiredSetting(configRegistry, "GitHub:Owner", "GitHub");
            configuration.GetRequiredSetting(configRegistry, "GitHub:Repository", "GitHub");
            configuration.GetRequiredSetting(configRegistry, "GitHub:AccessToken", "GitHub", isSensitive: true);

            // Host-owned: Shell's own Account/Login view reads DevAuth:Enabled.
            configuration.GetOptionalSetting(configRegistry, "DevAuth:Enabled", "Development");
            // Per-PR previews only, set by docker-entrypoint.sh; QA leaves it off.
            configuration.GetOptionalSetting(configRegistry, "DevAuth:AllowAdmin", "Development");

            // The five machine-API keys that used to live here (FEEDBACK_API_KEY,
            // ISSUES_API_KEY, SURVEY_API_KEY, LOG_API_KEY, AGENT_API_KEY) are gone:
            // /api/backdoor/* authenticates against per-person rows an admin allocates at
            // /Backdoor, not against deploy-time environment variables
            // (nobodies-collective/Humans#1128).

            foreach (var section in SectionDiscoveryExtensions.DiscoverImplementations<ISectionConfiguration>())
            {
                section.DeclareSettings(configuration, configRegistry);
            }
        }

        return services;
    }
}
