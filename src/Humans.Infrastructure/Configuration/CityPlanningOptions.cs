namespace Humans.Infrastructure.Configuration;

public class CityPlanningOptions
{
    public const string SectionName = "CityPlanning";

    /// <summary>
    /// Slug of the Team that has full map admin access (city planning team).
    /// Members of this team can always edit polygons and access the admin panel.
    /// </summary>
    public string CityPlanningTeamSlug { get; set; } = string.Empty;
}
