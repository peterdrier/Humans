using Humans.Finance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Finance.Data.Configurations;

internal sealed class HoldedDocSyncStateConfiguration : IEntityTypeConfiguration<HoldedDocSyncState>
{
    public void Configure(EntityTypeBuilder<HoldedDocSyncState> b)
    {
        b.ToTable("holded_doc_sync_state");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Status).HasMaxLength(16);
        b.Property(x => x.LastError).HasMaxLength(2000);
    }
}
