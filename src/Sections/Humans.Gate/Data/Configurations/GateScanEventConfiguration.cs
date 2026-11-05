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
            .HasMaxLength(GateScanEvent.BarcodeMaxLength);

        builder.Property(x => x.Verdict)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(40);

        builder.Property(x => x.LaneId).HasMaxLength(GateScanEvent.LaneIdMaxLength);
        builder.Property(x => x.Note).HasMaxLength(GateScanEvent.NoteMaxLength);
        builder.Property(x => x.AdmitDedupeKey).HasMaxLength(GateScanEvent.BarcodeMaxLength);

        // At most one admit per barcode across all lanes. Postgres excludes nulls, so non-admit rows never collide.
        builder.HasIndex(x => x.AdmitDedupeKey)
            .IsUnique()
            .HasDatabaseName("ix_gate_scan_events_admit_dedupe_key");

        builder.HasIndex(x => x.OccurredAt);
        builder.HasIndex(x => x.ScannedByUserId);
    }
}
