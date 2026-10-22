using Humans.Cantina.Data.Configurations;
using Humans.Cantina.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Cantina.Data;

internal sealed class CantinaDbContext(DbContextOptions<CantinaDbContext> options)
    : DbContext(options)
{
    public DbSet<DietaryProfile> DietaryProfiles => Set<DietaryProfile>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfiguration(new DietaryProfileConfiguration());
    }
}
