using Humans.Base.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Preapprovals.Data;

/// <summary>Same history table as the runtime registration, so a CI apply writes the table the app reads.</summary>
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
