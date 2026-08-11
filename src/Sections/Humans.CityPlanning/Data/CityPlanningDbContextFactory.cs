using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.CityPlanning.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef … --context CityPlanningDbContext</c>.
/// Mirrors Base's HumansDbContextFactory; the migrations-history table comes
/// from <see cref="SectionMigrationsHistory"/> — the same helper the runtime
/// registration uses — so CI's from-scratch apply records baselines in the table
/// the app reads.
/// </summary>
internal sealed class CityPlanningDbContextFactory : IDesignTimeDbContextFactory<CityPlanningDbContext>
{
    public CityPlanningDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<CityPlanningDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly("Humans.CityPlanning");
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<CityPlanningDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new CityPlanningDbContext(optionsBuilder.Options);
    }
}
