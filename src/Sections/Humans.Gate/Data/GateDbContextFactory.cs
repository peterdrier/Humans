using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Gate.Data;

internal sealed class GateDbContextFactory : SectionDesignTimeDbContextFactory<GateDbContext>
{
    protected override GateDbContext Create(DbContextOptions<GateDbContext> options) => new(options);
}
