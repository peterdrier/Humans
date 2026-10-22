using Humans.CityPlanning.Domain;
using Humans.CityPlanning.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.CityPlanning.Data;

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
