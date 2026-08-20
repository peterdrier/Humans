using Humans.MailerLite.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.MailerLite.Data.Configurations;

internal sealed class MailerLiteSyncStateConfiguration : IEntityTypeConfiguration<MailerLiteSyncState>
{
    public void Configure(EntityTypeBuilder<MailerLiteSyncState> b)
    {
        b.ToTable("mailerlite_sync_states");

        // Id-keyed, not Key-keyed: the audit row for a sync points at this Id, and identity
        // rests on ids only (memory/architecture/unique-constraints-ids-only.md). Key stays a
        // plain column — the repository is the contract that keeps it one row per key.
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(64);
        b.Property(x => x.Summary).HasMaxLength(1000);
        b.Property(x => x.GroupId).HasMaxLength(64);
        b.Property(x => x.GroupName).HasMaxLength(200);
    }
}
