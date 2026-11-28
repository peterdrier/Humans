using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Holded.Data;

internal sealed class HoldedDbContextFactory : SectionDesignTimeDbContextFactory<HoldedDbContext>
{
    protected override HoldedDbContext Create(DbContextOptions<HoldedDbContext> options) => new(options);
}
