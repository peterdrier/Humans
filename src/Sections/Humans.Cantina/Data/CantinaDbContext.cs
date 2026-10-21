using Humans.Cantina.Data.Configurations;
using Humans.Cantina.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Cantina.Data;

/// <summary>
/// Cantina's own context: <c>cantina_dietary_profiles</c> and its own
/// <c>__EFMigrationsHistory_Cantina</c>, same database and connection. Configurations are applied
/// explicitly, never by assembly scan, so this model cannot accrete another section's tables.
/// </summary>
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
