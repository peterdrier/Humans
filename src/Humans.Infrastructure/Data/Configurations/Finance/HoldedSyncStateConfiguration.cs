using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations.Finance;

public class HoldedSyncStateConfiguration : IEntityTypeConfiguration<HoldedSyncState>
{
    public void Configure(EntityTypeBuilder<HoldedSyncState> builder)
    {
        builder.ToTable("holded_sync_states");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id).ValueGeneratedNever(); // singleton, fixed Id = 1

        builder.Property(s => s.LastSyncAt);
        builder.Property(s => s.SyncStatus)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(s => s.LastError).HasMaxLength(2048);
        builder.Property(s => s.StatusChangedAt).IsRequired();
        builder.Property(s => s.LastSyncedDocCount).IsRequired();
    }
}
