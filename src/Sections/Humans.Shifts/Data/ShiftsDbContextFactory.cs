using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Shifts.Data;

internal sealed class ShiftsDbContextFactory : SectionDesignTimeDbContextFactory<ShiftsDbContext>
{
    protected override ShiftsDbContext Create(DbContextOptions<ShiftsDbContext> options) => new(options);
}
