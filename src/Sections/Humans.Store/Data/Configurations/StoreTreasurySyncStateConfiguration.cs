using Humans.Domain.Entities;
using Humans.Store.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Store.Data.Configurations;

public class StoreTreasurySyncStateConfiguration : IEntityTypeConfiguration<StoreTreasurySyncState>
{
    public void Configure(EntityTypeBuilder<StoreTreasurySyncState> b)
    {
        b.ToTable("store_treasury_sync_state");
        b.HasKey(x => x.Id);
        b.Property(x => x.SyncStatus).HasConversion<int>();
        b.Property(x => x.LastError).HasMaxLength(2000);
    }
}
