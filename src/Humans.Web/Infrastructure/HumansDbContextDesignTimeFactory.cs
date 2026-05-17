using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Web.Infrastructure;

public sealed class HumansDbContextDesignTimeFactory : IDesignTimeDbContextFactory<HumansDbContext>
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
