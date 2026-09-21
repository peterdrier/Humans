using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Humans.Users;

/// <summary>
/// Users' own settings. The Maps key is read by <c>ProfileController</c>'s address picker
/// (Teams' member map reads the same key through the same registration).
/// </summary>
internal sealed class SectionConfiguration : ISectionConfiguration
{
    public void DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetRequiredSetting(registry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);
    }
}
