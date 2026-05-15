using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Humans.Web.Infrastructure;

public sealed class HumansDbContextDesignTimeFactory : IDesignTimeDbContextFactory<HumansDbContext>
{
    public HumansDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<HumansDbContext>();
        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=humans_design_time;Username=humans;Password=humans",
            npgsqlOptions =>
            {
                npgsqlOptions.UseNodaTime();
                npgsqlOptions.MigrationsAssembly("Humans.Infrastructure");
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new HumansDbContext(optionsBuilder.Options);
    }
}
