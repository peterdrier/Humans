using Humans.Base.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Preapprovals.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef … --context PreapprovalsDbContext</c>. The
/// migrations-history table comes from <see cref="SectionMigrationsHistory"/>, the same helper
/// the runtime registration uses, so CI's from-scratch apply records in the table the app reads.
/// </summary>
internal sealed class PreapprovalsDbContextFactory : IDesignTimeDbContextFactory<PreapprovalsDbContext>
{
    public PreapprovalsDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<PreapprovalsDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly(typeof(PreapprovalsDbContext).Assembly.GetName().Name!);
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<PreapprovalsDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new PreapprovalsDbContext(optionsBuilder.Options);
    }
}
