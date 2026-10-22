using Humans.MailerLite.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.MailerLite.Data.Configurations;

internal sealed class MailerLiteSyncStateConfiguration : IEntityTypeConfiguration<MailerLiteSyncState>
{
    public void Configure(EntityTypeBuilder<MailerLiteSyncState> b)
    {
        b.ToTable("mailerlite_sync_states");

        // Keyed on Id: the sync's audit row points at it. The repository keeps Key to one row per key.
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(64);
        b.Property(x => x.Summary).HasMaxLength(1000);
        b.Property(x => x.GroupId).HasMaxLength(64);
        b.Property(x => x.GroupName).HasMaxLength(200);
    }
}
