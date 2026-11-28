using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Grants.Data;

internal sealed class GrantsDbContextFactory : SectionDesignTimeDbContextFactory<GrantsDbContext>
{
    protected override GrantsDbContext Create(DbContextOptions<GrantsDbContext> options) => new(options);
}
