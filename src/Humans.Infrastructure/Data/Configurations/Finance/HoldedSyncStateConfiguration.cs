using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Finance;

public class HoldedSyncStateConfiguration : IEntityTypeConfiguration<HoldedSyncState>
{
    public void Configure(EntityTypeBuilder<HoldedSyncState> b)
    {
        b.ToTable("holded_sync_states");
        b.HasKey(x => x.Id);
        b.Property(x => x.SyncStatus).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.LastError).HasMaxLength(2000);

        // Seed the singleton row
        b.HasData(new
        {
            Id = 1,
            SyncStatus = HoldedSyncStatus.Idle,
            LastSyncedDocCount = 0,
        });
    }
}
