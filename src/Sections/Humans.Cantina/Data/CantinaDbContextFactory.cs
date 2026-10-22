using Humans.Base.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Cantina.Data;

/// <summary>Same history table as the runtime registration, so a CI apply writes the table the app reads.</summary>
internal sealed class CantinaDbContextFactory : IDesignTimeDbContextFactory<CantinaDbContext>
{
    public CantinaDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<CantinaDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly("Humans.Cantina");
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<CantinaDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new CantinaDbContext(optionsBuilder.Options);
    }
}
