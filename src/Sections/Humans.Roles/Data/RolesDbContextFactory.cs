using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Roles.Data;

internal sealed class RolesDbContextFactory : SectionDesignTimeDbContextFactory<RolesDbContext>
{
    protected override RolesDbContext Create(DbContextOptions<RolesDbContext> options) => new(options);
}
