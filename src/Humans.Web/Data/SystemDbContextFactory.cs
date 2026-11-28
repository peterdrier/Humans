using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Web.Data;

internal sealed class SystemDbContextFactory : SectionDesignTimeDbContextFactory<SystemDbContext>
{
    protected override SystemDbContext Create(DbContextOptions<SystemDbContext> options) => new(options);
}
