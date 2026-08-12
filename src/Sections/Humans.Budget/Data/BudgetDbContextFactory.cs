using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Humans.Infrastructure.Data;

namespace Humans.Budget.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef … --context BudgetDbContext</c>.
/// The migrations-history table comes
/// from <see cref="SectionMigrationsHistory"/> — the same helper the runtime
/// registration uses — so CI's from-scratch apply records baselines in the table
/// the app reads.
/// </summary>
internal sealed class BudgetDbContextFactory : IDesignTimeDbContextFactory<BudgetDbContext>
{
    public BudgetDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<BudgetDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly("Humans.Budget");
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<BudgetDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new BudgetDbContext(optionsBuilder.Options);
    }
}
