using Humans.CityPlanning.Domain;
using Humans.CityPlanning.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.CityPlanning.Data;

/// <summary>
/// Per-section database context for the City Planning section: maps only
/// <c>city_planning_settings</c>,
/// <c>camp_polygons</c> and <c>camp_polygon_histories</c>, with its own
/// <c>__EFMigrationsHistory_CityPlanning</c> table and migrations under
/// <c>Migrations/CityPlanning/</c>. Same database, same connection — the split
/// is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like every section context: repositories are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// A polygon's camp season and its editing user are bare Guid references, so the
/// Camps and Identity tables stay outside this model and are deliberately absent
/// here.
/// </remarks>
internal sealed class CityPlanningDbContext(DbContextOptions<CityPlanningDbContext> options)
    : DbContext(options)
{
    public DbSet<CityPlanningSettings> CityPlanningSettings => Set<CityPlanningSettings>();
    public DbSet<CampPolygon> CampPolygons => Set<CampPolygon>();
    public DbSet<CampPolygonHistory> CampPolygonHistories => Set<CampPolygonHistory>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new CityPlanningSettingsConfiguration());
        builder.ApplyConfiguration(new CampPolygonConfiguration());
        builder.ApplyConfiguration(new CampPolygonHistoryConfiguration());
    }
}
