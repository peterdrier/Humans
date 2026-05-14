using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class EventVenueConfiguration : IEntityTypeConfiguration<EventVenue>
{
    public void Configure(EntityTypeBuilder<EventVenue> builder)
    {
        builder.ToTable("event_venues");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Name).HasMaxLength(120).IsRequired();
        builder.Property(v => v.Description).HasMaxLength(500);
        builder.Property(v => v.LocationDescription).HasMaxLength(120);

        builder.HasIndex(v => v.IsActive);
    }
}
