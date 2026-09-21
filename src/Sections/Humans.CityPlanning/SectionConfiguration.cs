using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Humans.CityPlanning;

/// <summary>
/// City Planning's own setting — the team slug behind <c>CityPlanningOptions</c>. Without it,
/// only admins can edit polygons.
/// </summary>
internal sealed class SectionConfiguration : ISectionConfiguration
{
    public void DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "CityPlanning:CityPlanningTeamSlug", "City Planning");
    }
}
