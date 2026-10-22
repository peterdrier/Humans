namespace Humans.CityPlanning.Contracts;

public sealed class CityPlanningOptions
{
    public const string SectionName = "CityPlanning";

    /// <summary>Members of this team can always edit polygons and use the map admin panel.</summary>
    public string CityPlanningTeamSlug { get; set; } = string.Empty;
}
