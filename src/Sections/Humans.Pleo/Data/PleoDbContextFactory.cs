using Humans.Base.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Pleo.Data;

/// <summary>Same history table as the runtime registration, so a CI apply writes the table the app reads.</summary>
internal sealed class PleoDbContextFactory : IDesignTimeDbContextFactory<PleoDbContext>
{
    public PleoDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<PleoDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly("Humans.Pleo");
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<PleoDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new PleoDbContext(optionsBuilder.Options);
    }
}
