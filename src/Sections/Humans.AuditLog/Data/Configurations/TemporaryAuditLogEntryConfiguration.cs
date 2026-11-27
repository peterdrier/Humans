using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.Domain;

namespace Humans.AuditLog.Data.Configurations;

/// <summary>No triggers: unlike <c>audit_log_entries</c>, rows here are deleted by age and by erasure.</summary>
internal sealed class TemporaryAuditLogEntryConfiguration : IEntityTypeConfiguration<TemporaryAuditLogEntry>
{
    public void Configure(EntityTypeBuilder<TemporaryAuditLogEntry> builder)
    {
        builder.ToTable("audit_log_temporary_entries");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Action)
            .HasMaxLength(AuditActionDefinition.NameMaxLength)
            .IsRequired();

        builder.Property(e => e.EntityType)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(e => e.Description)
            .IsRequired();

        builder.Property(e => e.RelatedEntityType)
            .HasMaxLength(100);

        builder.HasIndex(e => new { e.EntityType, e.EntityId });
        builder.HasIndex(e => new { e.RelatedEntityType, e.RelatedEntityId });
        builder.HasIndex(e => e.OccurredAt);
        builder.HasIndex(e => e.Action);
        builder.HasIndex(e => e.ActorUserId);
    }
}
