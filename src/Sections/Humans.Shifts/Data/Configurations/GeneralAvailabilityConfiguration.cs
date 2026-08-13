using Humans.Domain.Entities;
using Humans.Shifts.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Shifts.Data.Configurations;

internal sealed class GeneralAvailabilityConfiguration : IEntityTypeConfiguration<GeneralAvailability>
{
    public void Configure(EntityTypeBuilder<GeneralAvailability> builder)
    {
        builder.ToTable("general_availability");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.AvailableDayOffsets)
            .HasColumnType("jsonb")
            .Metadata.SetValueComparer(new ValueComparer<List<int>>(
                (a, b) => a != null && b != null && a.SequenceEqual(b),
                c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                c => c.ToList()));

        builder.HasIndex(e => new { e.UserId, e.EventSettingsId }).IsUnique();

        // UserId is a bare cross-section Guid column — no FK constraint, no nav.

        builder.HasOne(e => e.EventSettings)
            .WithMany()
            .HasForeignKey(e => e.EventSettingsId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
