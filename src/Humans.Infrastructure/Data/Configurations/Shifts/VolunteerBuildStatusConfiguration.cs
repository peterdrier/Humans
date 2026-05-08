using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Shifts;

internal sealed class VolunteerBuildStatusConfiguration
    : IEntityTypeConfiguration<VolunteerBuildStatus>
{
    public void Configure(EntityTypeBuilder<VolunteerBuildStatus> builder)
    {
        builder.ToTable("volunteer_build_statuses");

        builder.HasKey(x => x.Id);

        // Cross-section: bare Guid, NO HasOne<User>() — per
        // memory/architecture/no-cross-section-ef-joins.md.
        builder.Property(x => x.UserId).IsRequired();

        // Same-section FK to event_settings; no nav property on the entity.
        builder.HasOne<EventSettings>()
            .WithMany()
            .HasForeignKey(x => x.EventSettingsId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.BarrioSetupStartDate);

        builder.Property(x => x.BlockedDayOffsets)
            .HasColumnType("jsonb");

        builder.Property(x => x.Notes).HasMaxLength(500);

        builder.Property(x => x.SetByUserId);
        builder.Property(x => x.SetAt);

        builder.HasIndex(x => new { x.UserId, x.EventSettingsId })
            .IsUnique();
    }
}
