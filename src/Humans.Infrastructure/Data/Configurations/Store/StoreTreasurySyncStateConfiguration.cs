using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Store;

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
