using Humans.Base.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Workgroups.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef … --context WorkgroupsDbContext</c>.
/// The migrations-history table comes from <see cref="SectionMigrationsHistory"/> — the
/// same helper the runtime registration uses — so CI's from-scratch apply records
/// baselines in the table the app reads.
/// </summary>
internal sealed class WorkgroupsDbContextFactory : IDesignTimeDbContextFactory<WorkgroupsDbContext>
{
    public WorkgroupsDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<WorkgroupsDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly(typeof(WorkgroupsDbContext).Assembly.GetName().Name!);
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<WorkgroupsDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new WorkgroupsDbContext(optionsBuilder.Options);
    }
}
