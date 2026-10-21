using Humans.Base.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Grants.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef … --context GrantsDbContext</c>. The
/// migrations-history table comes from <see cref="SectionMigrationsHistory"/>, the same helper
/// the runtime registration uses, so CI's from-scratch apply records in the table the app reads.
/// </summary>
internal sealed class GrantsDbContextFactory : IDesignTimeDbContextFactory<GrantsDbContext>
{
    public GrantsDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<GrantsDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly(typeof(GrantsDbContext).Assembly.GetName().Name!);
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<GrantsDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new GrantsDbContext(optionsBuilder.Options);
    }
}
