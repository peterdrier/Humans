using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Rideshare.Data;

internal sealed class RideshareDbContextFactory : SectionDesignTimeDbContextFactory<RideshareDbContext>
{
    protected override RideshareDbContext Create(DbContextOptions<RideshareDbContext> options) => new(options);
}
