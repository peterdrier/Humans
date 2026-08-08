using Humans.Store.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Store.Data.Configurations;

internal sealed class TreasurySyncStateConfiguration : IEntityTypeConfiguration<TreasurySyncState>
{
    public void Configure(EntityTypeBuilder<TreasurySyncState> b)
    {
        b.ToTable("store_treasury_sync_state");
        b.HasKey(x => x.Id);
        b.Property(x => x.SyncStatus).HasConversion<int>();
        b.Property(x => x.LastError).HasMaxLength(2000);
    }
}
