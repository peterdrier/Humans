using Humans.Holded.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Holded.Data.Configurations;

internal sealed class HoldedSyncStateConfiguration : IEntityTypeConfiguration<HoldedSyncState>
{
    public void Configure(EntityTypeBuilder<HoldedSyncState> b)
    {
        b.ToTable("holded_sync_states");
        // Keyed on the kind: one row per sync, lazy-created on first use. No HasData —
        // a seeded row would claim a sync had a state before one ever ran.
        b.HasKey(x => x.Kind);
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.SyncStatus).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.LastError).HasMaxLength(2000);
    }
}
