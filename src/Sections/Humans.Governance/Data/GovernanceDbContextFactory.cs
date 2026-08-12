using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Governance.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef … --context GovernanceDbContext</c>.
/// The migrations-history table comes
/// from <see cref="SectionMigrationsHistory"/> — the same helper the runtime
/// registration uses — so CI's from-scratch apply records baselines in the table
/// the app reads.
/// </summary>
internal sealed class GovernanceDbContextFactory : IDesignTimeDbContextFactory<GovernanceDbContext>
{
    public GovernanceDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<GovernanceDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly("Humans.Governance");
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<GovernanceDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new GovernanceDbContext(optionsBuilder.Options);
    }
}
