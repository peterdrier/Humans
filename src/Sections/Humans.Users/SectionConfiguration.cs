using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Humans.Users;

/// <summary>Teams' member map reads the same Maps key through this registration.</summary>
internal sealed class SectionConfiguration : ISectionConfiguration
{
    public void DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetRequiredSetting(registry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
    }
}
