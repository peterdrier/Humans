using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef migrations</c>. Internal because
/// <see cref="HumansDbContext"/> itself is internal; EF tooling locates this
/// type via reflection in the migrations assembly.
/// </summary>
internal sealed class HumansDbContextFactory : IDesignTimeDbContextFactory<HumansDbContext>
{
    public HumansDbContext CreateDbContext(string[] args)
    {
        // Prefer the standard ASP.NET Core env var when set. CI's
        // verify-migrations-apply job runs from a container where localhost is
        // not the sibling Postgres container.
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
