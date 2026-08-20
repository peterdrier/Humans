using Humans.Base.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.MailerLite.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef … --context MailerLiteDbContext</c>.
/// Mirrors <c>HoldedDbContextFactory</c>; the migrations-history table comes from
/// <see cref="SectionMigrationsHistory"/> — the same helper the runtime registration uses —
/// so CI's from-scratch apply records baselines in the table the app reads.
/// </summary>
internal sealed class MailerLiteDbContextFactory : IDesignTimeDbContextFactory<MailerLiteDbContext>
{
    public MailerLiteDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<MailerLiteDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly("Humans.MailerLite");
                npgsqlOptions.MigrationsHistoryTable(
                    SectionMigrationsHistory.TableFor<MailerLiteDbContext>());
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new MailerLiteDbContext(optionsBuilder.Options);
    }
}
