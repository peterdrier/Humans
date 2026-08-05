using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NodaTime;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Application.Architecture;

namespace Humans.Infrastructure.Data.Configurations;

[Grandfathered(
    ruleId: "HUM0024",
    justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.",
    since: "2026-08-05",
    issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]
public class SyncServiceSettingsConfiguration : IEntityTypeConfiguration<SyncServiceSettings>
{
    private static readonly Instant SeedTimestamp = Instant.FromUtc(2026, 3, 9, 0, 0, 0);

    public void Configure(EntityTypeBuilder<SyncServiceSettings> builder)
    {
        builder.ToTable("sync_service_settings");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.ServiceType)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(s => s.SyncMode)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(s => s.UpdatedAt)
            .IsRequired();

        builder.HasIndex(s => s.ServiceType)
            .IsUnique();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(s => s.UpdatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Reserved GUID block: 0002. See docs/guid-reservations.md.
        // Seed one row per service type, all defaulting to None.
        builder.HasData(
            new
            {
                Id = Guid.Parse("00000000-0000-0000-0002-000000000001"),
                ServiceType = SyncServiceType.GoogleDrive,
                SyncMode = SyncMode.None,
                UpdatedAt = SeedTimestamp,
            },
            new
            {
                Id = Guid.Parse("00000000-0000-0000-0002-000000000002"),
                ServiceType = SyncServiceType.GoogleGroups,
                SyncMode = SyncMode.None,
                UpdatedAt = SeedTimestamp,
            },
            new
            {
                Id = Guid.Parse("00000000-0000-0000-0002-000000000003"),
                ServiceType = SyncServiceType.Discord,
                SyncMode = SyncMode.None,
                UpdatedAt = SeedTimestamp,
            });
    }
}
