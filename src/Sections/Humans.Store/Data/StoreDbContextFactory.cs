using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Store.Data;

internal sealed class StoreDbContextFactory : SectionDesignTimeDbContextFactory<StoreDbContext>
{
    protected override StoreDbContext Create(DbContextOptions<StoreDbContext> options) => new(options);
}
