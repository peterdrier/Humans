using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Camps.Data;

internal sealed class CampsDbContextFactory : SectionDesignTimeDbContextFactory<CampsDbContext>
{
    protected override CampsDbContext Create(DbContextOptions<CampsDbContext> options) => new(options);
}
