using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Humans.CityPlanning;

/// <summary>Without the team slug, only admins can edit polygons.</summary>
internal sealed class SectionConfiguration : ISectionConfiguration
{
    public void DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "CityPlanning:CityPlanningTeamSlug", "City Planning");
    }
}
