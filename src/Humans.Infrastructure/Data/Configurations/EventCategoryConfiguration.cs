using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class EventCategoryConfiguration : IEntityTypeConfiguration<EventCategory>
{
    public void Configure(EntityTypeBuilder<EventCategory> builder)
    {
        builder.ToTable("event_categories");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).HasMaxLength(60).IsRequired();
        builder.Property(c => c.Slug).HasMaxLength(60).IsRequired();

        builder.HasIndex(c => c.Slug).IsUnique();
        builder.HasIndex(c => c.IsActive);

        builder.HasData(
            new { Id = Guid.Parse("00000000-0000-0000-0026-000000000001"), Name = "Workshop", Slug = "workshop", IsSensitive = false, IsActive = true, DisplayOrder = 1 },
            new { Id = Guid.Parse("00000000-0000-0000-0026-000000000002"), Name = "Party", Slug = "party", IsSensitive = false, IsActive = true, DisplayOrder = 2 },
            new { Id = Guid.Parse("00000000-0000-0000-0026-000000000003"), Name = "Food and drink", Slug = "food-and-drink", IsSensitive = false, IsActive = true, DisplayOrder = 3 },
            new { Id = Guid.Parse("00000000-0000-0000-0026-000000000004"), Name = "Chillout", Slug = "chillout", IsSensitive = false, IsActive = true, DisplayOrder = 4 },
            new { Id = Guid.Parse("00000000-0000-0000-0026-000000000005"), Name = "Spiritual / Healing", Slug = "spiritual-healing", IsSensitive = true, IsActive = true, DisplayOrder = 5 },
            new { Id = Guid.Parse("00000000-0000-0000-0026-000000000006"), Name = "Other", Slug = "other", IsSensitive = false, IsActive = true, DisplayOrder = 6 }
        );
    }
}
