using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Cantina.Data;

internal sealed class CantinaDbContextFactory : SectionDesignTimeDbContextFactory<CantinaDbContext>
{
    protected override CantinaDbContext Create(DbContextOptions<CantinaDbContext> options) => new(options);
}
