using Microsoft.EntityFrameworkCore;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Issues.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef … --context IssuesDbContext</c>.
/// Mirrors <see cref="HumansDbContextFactory"/>; the migrations-history table comes
/// from <see cref="SectionMigrationsHistory"/> — the same helper the runtime
/// registration uses — so CI's from-scratch apply records baselines in the table
/// the app reads.
/// </summary>
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
