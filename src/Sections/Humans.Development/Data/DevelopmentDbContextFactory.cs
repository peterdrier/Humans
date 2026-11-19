using Humans.Base.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Development.Data;

/// <summary>Same history table as the runtime registration, so a CI apply writes the table the app reads.</summary>
internal sealed class DevelopmentDbContextFactory : IDesignTimeDbContextFactory<DevelopmentDbContext>
{
    public DevelopmentDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<DevelopmentDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly(typeof(DevelopmentDbContext).Assembly.GetName().Name!);
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<DevelopmentDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new DevelopmentDbContext(optionsBuilder.Options);
    }
}
