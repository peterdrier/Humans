using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Humans.Agent;

internal sealed class SectionConfiguration : ISectionConfiguration
{
    public void DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "Anthropic:ApiKey", "Anthropic", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "Anthropic:DefaultModel", "Anthropic");

        configuration.GetOptionalSetting(registry, "CommunityKb:Owner", "Community KB");
        configuration.GetOptionalSetting(registry, "CommunityKb:Repository", "Community KB");
        configuration.GetOptionalSetting(registry, "CommunityKb:Branch", "Community KB");
        configuration.GetOptionalSetting(registry, "CommunityKb:AccessToken", "Community KB", isSensitive: true);
    }
}
