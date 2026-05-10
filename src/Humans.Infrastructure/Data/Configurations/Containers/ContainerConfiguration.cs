using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Containers;

public class ContainerConfiguration : IEntityTypeConfiguration<Container>
{
    public void Configure(EntityTypeBuilder<Container> builder)
    {
        builder.ToTable("containers");

        builder.Property(c => c.Name).HasMaxLength(256).IsRequired();
        builder.Property(c => c.Description).HasMaxLength(2000);
        builder.Property(c => c.ImageStoragePath).HasMaxLength(512);
        builder.Property(c => c.ImageContentType).HasMaxLength(64);
        builder.Property(c => c.ImageFileName).HasMaxLength(256);
        builder.Property(c => c.LocationGeoJson).HasColumnType("text");
        builder.Property(c => c.PlacementNotes).HasMaxLength(5000);
        builder.Property(c => c.PlacementImageStoragePath).HasMaxLength(512);
        builder.Property(c => c.PlacementImageContentType).HasMaxLength(64);
        builder.Property(c => c.PlacementImageFileName).HasMaxLength(256);

        // Bare FK column (no HasOne / nav) — Camp lives in a different section.
        // See memory/architecture/no-cross-section-ef-joins.md.
        builder.HasIndex(c => c.CampId);
        builder.HasIndex(c => c.Year);
    }
}
