using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.CityPlanning.Data;

internal sealed class CityPlanningDbContextFactory : SectionDesignTimeDbContextFactory<CityPlanningDbContext>
{
    protected override CityPlanningDbContext Create(DbContextOptions<CityPlanningDbContext> options) => new(options);
}
