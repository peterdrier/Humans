using Humans.Gate.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Gate.Data.Configurations;

internal sealed class GateScanEventConfiguration : IEntityTypeConfiguration<GateScanEvent>
{
    public void Configure(EntityTypeBuilder<GateScanEvent> builder)
    {
        builder.ToTable("gate_scan_events");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.OccurredAt).IsRequired();
        builder.Property(x => x.ScannedByUserId).IsRequired();

        builder.Property(x => x.Barcode)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.Verdict)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(40);

        builder.Property(x => x.LaneId).HasMaxLength(64);
        builder.Property(x => x.Note).HasMaxLength(1000);
        builder.Property(x => x.AdmitDedupeKey).HasMaxLength(128);

        // Cross-section links are bare Guid columns (no nav, no FK) per
        // no-cross-section-EF-joins — TicketAttendeeId, GuestUserId, OverrideByUserId.

        // Atomic duplicate guard: at most one admit per barcode across all lanes.
        // Postgres excludes NULLs from unique indexes, so reject/unresolved rows
        // (AdmitDedupeKey == null) never collide.
        builder.HasIndex(x => x.AdmitDedupeKey)
            .IsUnique()
            .HasDatabaseName("ix_gate_scan_events_admit_dedupe_key");

        // Leaderboard / audit query paths.
        builder.HasIndex(x => x.OccurredAt);
        builder.HasIndex(x => x.ScannedByUserId);
    }
}
