using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Containers;

public class ContainerPlacementConfiguration : IEntityTypeConfiguration<ContainerPlacement>
{
    public void Configure(EntityTypeBuilder<ContainerPlacement> builder)
    {
        builder.ToTable("container_placements");

        builder.HasKey(p => new { p.ContainerId, p.Year });

        builder.Property(p => p.LocationGeoJson).HasColumnType("text");
        builder.Property(p => p.PlacementNotes).HasMaxLength(5000);
        builder.Property(p => p.PlacementImageStoragePath).HasMaxLength(512);
        builder.Property(p => p.PlacementImageContentType).HasMaxLength(64);
        builder.Property(p => p.PlacementImageFileName).HasMaxLength(256);

        builder.HasIndex(p => p.Year);
    }
}
