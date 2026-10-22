using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Humans.Holded;

/// <summary>BaseUrl has a default; the key is env-var only.</summary>
internal sealed class SectionConfiguration : ISectionConfiguration
{
    public void DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "Holded:BaseUrl", "Holded");
        registry.RegisterEnvironmentVariable("HOLDED_API_KEY_V2", "Holded", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
    }
}
