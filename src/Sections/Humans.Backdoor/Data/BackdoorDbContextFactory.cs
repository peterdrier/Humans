using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Backdoor.Data;

internal sealed class BackdoorDbContextFactory : SectionDesignTimeDbContextFactory<BackdoorDbContext>
{
    protected override BackdoorDbContext Create(DbContextOptions<BackdoorDbContext> options) => new(options);
}
