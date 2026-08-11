using Microsoft.EntityFrameworkCore;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Feedback.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef … --context FeedbackDbContext</c>.
/// Mirrors <see cref="HumansDbContextFactory"/>; the migrations-history table comes
/// from <see cref="SectionMigrationsHistory"/> — the same helper the runtime
/// registration uses — so CI's from-scratch apply records baselines in the table
/// the app reads.
/// </summary>
internal sealed class FeedbackDbContextFactory : IDesignTimeDbContextFactory<FeedbackDbContext>
{
    public FeedbackDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<FeedbackDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly("Humans.Feedback");
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<FeedbackDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new FeedbackDbContext(optionsBuilder.Options);
    }
}
