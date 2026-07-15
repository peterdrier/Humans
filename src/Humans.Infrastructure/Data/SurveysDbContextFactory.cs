using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef … --context SurveysDbContext</c>.
/// Mirrors <see cref="HumansDbContextFactory"/>; the migrations-history table must
/// match the runtime registration so CI's from-scratch apply records baselines in
/// <c>__EFMigrationsHistory_Surveys</c>.
/// </summary>
internal sealed class SurveysDbContextFactory : IDesignTimeDbContextFactory<SurveysDbContext>
{
    public SurveysDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<SurveysDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly("Humans.Infrastructure");
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory_Surveys");
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new SurveysDbContext(optionsBuilder.Options);
    }
}
