using System.Text.Json;
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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

        builder.Property(x => x.Notes).HasMaxLength(500);

        builder.Property(x => x.SetByUserId);
        builder.Property(x => x.SetAt);

        // List<DayOffEntry> is a collection of records — neither Npgsql's
        // automatic jsonb mapping nor EF's primitive-collection mapping
        // handle it, so we serialize explicitly with System.Text.Json.
        // The same converter is applied for both the Postgres jsonb column
        // and the InMemory provider used in unit tests.
        builder.Property(x => x.DayOffs)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'[]'::jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => string.IsNullOrEmpty(v)
                    ? new List<DayOffEntry>()
                    : (JsonSerializer.Deserialize<List<DayOffEntry>>(v, (JsonSerializerOptions?)null) ?? new List<DayOffEntry>()))
            .Metadata.SetValueComparer(new ValueComparer<List<DayOffEntry>>(
                (a, b) => a != null && b != null && a.SequenceEqual(b),
                c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                c => c.ToList()));

        builder.HasIndex(x => new { x.UserId, x.EventSettingsId })
            .IsUnique();
    }
}
