using Humans.Base.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Rideshare.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef … --context RideshareDbContext</c>.
/// The migrations-history table comes
/// from <see cref="SectionMigrationsHistory"/> — the same helper the runtime
/// registration uses — so CI's from-scratch apply records baselines in the table
/// the app reads.
/// </summary>
internal sealed class RideshareDbContextFactory : IDesignTimeDbContextFactory<RideshareDbContext>
{
    public RideshareDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<RideshareDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly(typeof(RideshareDbContext).Assembly.GetName().Name!);
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<RideshareDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new RideshareDbContext(optionsBuilder.Options);
    }
}
