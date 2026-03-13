using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class BarrioImageConfiguration : IEntityTypeConfiguration<BarrioImage>
{
    public void Configure(EntityTypeBuilder<BarrioImage> builder)
    {
        builder.ToTable("barrio_images");

        builder.Property(i => i.FileName).HasMaxLength(256).IsRequired();
        builder.Property(i => i.StoragePath).HasMaxLength(512).IsRequired();
        builder.Property(i => i.ContentType).HasMaxLength(64).IsRequired();
    }
}
