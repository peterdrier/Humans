using Humans.Base.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Holded.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef … --context HoldedDbContext</c>.
/// Mirrors <c>FinanceDbContextFactory</c>; the migrations-history table comes from
/// <see cref="SectionMigrationsHistory"/> — the same helper the runtime registration uses —
/// so CI's from-scratch apply records baselines in the table the app reads.
/// </summary>
internal sealed class HoldedDbContextFactory : IDesignTimeDbContextFactory<HoldedDbContext>
{
    public HoldedDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<HoldedDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly("Humans.Holded");
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<HoldedDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new HoldedDbContext(optionsBuilder.Options);
    }
}
