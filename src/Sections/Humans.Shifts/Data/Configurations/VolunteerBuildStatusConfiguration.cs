using System.Text.Json;
using Humans.Shifts.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

namespace Humans.Shifts.Data.Configurations;

internal sealed class VolunteerBuildStatusConfiguration
    : IEntityTypeConfiguration<VolunteerBuildStatus>
{
    // System.Text.Json does not understand NodaTime's Instant: without these converters every read after a write
    // would deserialize MarkedAt as Instant.MinValue.
    private static readonly JsonSerializerOptions JsonOptions =
        new JsonSerializerOptions().ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);

    public void Configure(EntityTypeBuilder<VolunteerBuildStatus> builder)
    {
        builder.ToTable("volunteer_build_statuses");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId).IsRequired();

        builder.HasOne<EventSettings>()
            .WithMany()
            .HasForeignKey(x => x.EventSettingsId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.BarrioSetupStartDate);

        builder.Property(x => x.Notes).HasMaxLength(500);

        builder.Property(x => x.SetByUserId);
        builder.Property(x => x.SetAt);

        // A collection of records: neither Npgsql's jsonb mapping nor EF's primitive collections handle it, so it is
        // serialized explicitly, for Postgres and the InMemory test provider alike.
        builder.Property(x => x.DayOffs)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'[]'::jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonOptions),
                v => string.IsNullOrEmpty(v)
                    ? new List<DayOffEntry>()
                    : (JsonSerializer.Deserialize<List<DayOffEntry>>(v, JsonOptions) ?? new List<DayOffEntry>()))
            .Metadata.SetValueComparer(new ValueComparer<List<DayOffEntry>>(
                (a, b) => a != null && b != null && a.SequenceEqual(b),
                c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                c => c.ToList()));

        builder.HasIndex(x => new { x.UserId, x.EventSettingsId })
            .IsUnique();
    }
}
