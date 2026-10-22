using Humans.Base.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Gate.Data;

/// <summary>Same history table as the runtime registration, so a CI apply writes the table the app reads.</summary>
internal sealed class GateDbContextFactory : IDesignTimeDbContextFactory<GateDbContext>
{
    public GateDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<GateDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly("Humans.Gate");
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<GateDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new GateDbContext(optionsBuilder.Options);
    }
}
