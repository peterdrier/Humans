using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Workgroups.Data;

internal sealed class WorkgroupsDbContextFactory : SectionDesignTimeDbContextFactory<WorkgroupsDbContext>
{
    protected override WorkgroupsDbContext Create(DbContextOptions<WorkgroupsDbContext> options) => new(options);
}
