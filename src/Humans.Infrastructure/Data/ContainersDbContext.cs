using Humans.Domain.Entities;
using Humans.Infrastructure.Data.Configurations.Containers;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Per-section database context for the Containers section
/// (nobodies-collective/Humans#858): maps only <c>containers</c> and
/// <c>container_placements</c>, with its own
/// <c>__EFMigrationsHistory_Containers</c> table and migrations under
/// <c>Migrations/Containers/</c>. Same database, same connection — the split
/// is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like <see cref="HumansDbContext"/> (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// </remarks>
internal sealed class ContainersDbContext(DbContextOptions<ContainersDbContext> options)
    : DbContext(options)
{
    public DbSet<Container> Containers => Set<Container>();
    public DbSet<ContainerPlacement> ContainerPlacements => Set<ContainerPlacement>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new ContainerConfiguration());
        builder.ApplyConfiguration(new ContainerPlacementConfiguration());
    }
}
