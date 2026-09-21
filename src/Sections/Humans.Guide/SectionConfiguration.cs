using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Humans.Guide;

/// <summary>Guide's own settings — the GitHub folder its content is rendered from.</summary>
internal sealed class SectionConfiguration : ISectionConfiguration
{
    public void DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "Guide:Owner", "Guide");
        configuration.GetOptionalSetting(registry, "Guide:Repository", "Guide");
        configuration.GetOptionalSetting(registry, "Guide:Branch", "Guide");
        configuration.GetOptionalSetting(registry, "Guide:FolderPath", "Guide");
        configuration.GetOptionalSetting(registry, "Guide:CacheTtlHours", "Guide");
        configuration.GetOptionalSetting(registry, "Guide:AccessToken", "Guide", isSensitive: true);
    }
}
