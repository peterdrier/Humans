using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Pleo.Data;

internal sealed class PleoDbContextFactory : SectionDesignTimeDbContextFactory<PleoDbContext>
{
    protected override PleoDbContext Create(DbContextOptions<PleoDbContext> options) => new(options);
}
