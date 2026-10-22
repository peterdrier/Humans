using Microsoft.EntityFrameworkCore;
using Humans.Base.Data;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Issues.Data;

/// <summary>Same history table as the runtime registration, so a CI apply writes the table the app reads.</summary>
internal sealed class IssuesDbContextFactory : IDesignTimeDbContextFactory<IssuesDbContext>
{
    public IssuesDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<IssuesDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly("Humans.Issues");
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<IssuesDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new IssuesDbContext(optionsBuilder.Options);
    }
}
