using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Finance.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef … --context FinanceDbContext</c>.
/// Mirrors <see cref="HumansDbContextFactory"/>; the migrations-history table comes
/// from <see cref="SectionMigrationsHistory"/> — the same helper the runtime
/// registration uses — so CI's from-scratch apply records baselines in the table
/// the app reads.
/// </summary>
internal sealed class FinanceDbContextFactory : IDesignTimeDbContextFactory<FinanceDbContext>
{
    public FinanceDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<FinanceDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly("Humans.Finance");
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<FinanceDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new FinanceDbContext(optionsBuilder.Options);
    }
}
