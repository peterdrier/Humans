using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Humans.Base.Data;

namespace Humans.GoogleIntegration.Data;

/// <summary>Same history table as the runtime registration, so a CI apply writes the table the app reads.</summary>
internal sealed class GoogleIntegrationDbContextFactory : IDesignTimeDbContextFactory<GoogleIntegrationDbContext>
{
    public GoogleIntegrationDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<GoogleIntegrationDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly("Humans.GoogleIntegration");
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<GoogleIntegrationDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new GoogleIntegrationDbContext(optionsBuilder.Options);
    }
}
