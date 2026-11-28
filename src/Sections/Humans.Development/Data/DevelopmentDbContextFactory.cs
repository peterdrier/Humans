using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Development.Data;

internal sealed class DevelopmentDbContextFactory : SectionDesignTimeDbContextFactory<DevelopmentDbContext>
{
    protected override DevelopmentDbContext Create(DbContextOptions<DevelopmentDbContext> options) => new(options);
}
