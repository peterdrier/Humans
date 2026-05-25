using Humans.Application.Architecture;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef migrations</c>. Internal because
/// <see cref="HumansDbContext"/> itself is internal — the EF tooling locates
/// this type via reflection in the migrations-assembly, no public surface
/// needed.
/// </summary>
[Grandfathered(
    ruleId: "HUM0009",
    justification: "Design-time factory IS the persistence boundary — it constructs HumansDbContext for `dotnet ef migrations` tooling. There is no repository to route through. Follow-up to teach the analyzer about design-time-factory roles in #750.",
    since: "2026-05-17",
    issueRef: "nobodies-collective/Humans#750")]
internal sealed class HumansDbContextFactory : IDesignTimeDbContextFactory<HumansDbContext>
{
    public HumansDbContext CreateDbContext(string[] args)
    {
        // Prefer the standard ASP.NET Core env var when set (CI's
        // verify-migrations-apply job in .github/workflows/build.yml sets this
        // to point at a sibling Postgres container — localhost is unreachable
        // from the runner container). Fall back to a localhost dev string so
        // `dotnet ef migrations add` keeps working locally without any setup.
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

        var optionsBuilder = new DbContextOptionsBuilder<HumansDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly("Humans.Infrastructure");
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new HumansDbContext(optionsBuilder.Options);
    }
}
