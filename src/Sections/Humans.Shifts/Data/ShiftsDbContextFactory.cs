using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Humans.Shifts.Data;

namespace Humans.Shifts.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef … --context ShiftsDbContext</c>.
/// The migrations-history table comes
/// from <see cref="SectionMigrationsHistory"/> — the same helper the runtime
/// registration uses — so CI's from-scratch apply records baselines in the table
/// the app reads.
/// </summary>
internal sealed class ShiftsDbContextFactory : IDesignTimeDbContextFactory<ShiftsDbContext>
{
    public ShiftsDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<ShiftsDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly("Humans.Shifts");
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<ShiftsDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new ShiftsDbContext(optionsBuilder.Options);
    }
}
