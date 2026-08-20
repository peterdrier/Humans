using Humans.Containers.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Containers.Data.Configurations;

internal sealed class ContainerImageConfiguration : IEntityTypeConfiguration<ContainerImage>
{
    public void Configure(EntityTypeBuilder<ContainerImage> builder)
    {
        builder.ToTable("container_images");

        builder.Property(i => i.StoragePath).HasMaxLength(512).IsRequired();
        builder.Property(i => i.ContentType).HasMaxLength(64).IsRequired();
        builder.Property(i => i.FileName).HasMaxLength(256).IsRequired();

        // Same-section owner, so a real FK with cascade — no nav on either side,
        // images are loaded and written through their own repository methods.
        builder.HasOne<Container>()
            .WithMany()
            .HasForeignKey(i => i.ContainerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
