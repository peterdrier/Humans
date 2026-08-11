namespace Humans.CityPlanning.Contracts;

/// <summary>
/// On the contracts leaf rather than inside the section because Shell's
/// <c>DevPersonaSeeder</c> reads <see cref="CityPlanningTeamSlug"/> to seed the dev
/// city-planning team, and <c>ConfigurationMetadataExtensions</c> surfaces the same key
/// on the admin config page.
/// </summary>
public sealed class CityPlanningOptions
{
    public const string SectionName = "CityPlanning";

    /// <summary>
    /// Slug of the Team that has full map admin access (city planning team).
    /// Members of this team can always edit polygons and access the admin panel.
    /// </summary>
    public string CityPlanningTeamSlug { get; set; } = string.Empty;
}
