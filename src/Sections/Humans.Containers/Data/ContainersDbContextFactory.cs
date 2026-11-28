using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Containers.Data;

internal sealed class ContainersDbContextFactory : SectionDesignTimeDbContextFactory<ContainersDbContext>
{
    protected override ContainersDbContext Create(DbContextOptions<ContainersDbContext> options) => new(options);
}
