using Humans.Containers.Domain;
using Humans.Containers.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Containers.Data;

internal sealed class ContainersDbContext(DbContextOptions<ContainersDbContext> options)
    : DbContext(options)
{
    public DbSet<Container> Containers => Set<Container>();
    public DbSet<ContainerImage> ContainerImages => Set<ContainerImage>();
    public DbSet<ContainerPlacement> ContainerPlacements => Set<ContainerPlacement>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new ContainerConfiguration());
        builder.ApplyConfiguration(new ContainerImageConfiguration());
        builder.ApplyConfiguration(new ContainerPlacementConfiguration());
    }
}
