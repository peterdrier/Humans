using Humans.Base.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Backdoor.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef … --context BackdoorDbContext</c>. The
/// migrations-history table comes from <see cref="SectionMigrationsHistory"/> — the same
/// helper the runtime registration uses — so CI's from-scratch apply records baselines in
/// the table the app reads.
/// </summary>
internal sealed class BackdoorDbContextFactory : IDesignTimeDbContextFactory<BackdoorDbContext>
{
    public BackdoorDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<BackdoorDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly("Humans.Backdoor");
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<BackdoorDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new BackdoorDbContext(optionsBuilder.Options);
    }
}
