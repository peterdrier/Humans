using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Audit.Contracts;
using Humans.Audit.Domain;

namespace Humans.Audit.Data.Configurations;

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

        builder.Property(e => e.SubjectType)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(e => e.Description)
            .IsRequired();

        builder.Property(e => e.TargetType)
            .HasMaxLength(100);

        builder.Property(e => e.JobName)
            .HasMaxLength(100);

        builder.HasIndex(e => new { e.SubjectType, e.SubjectId });
        builder.HasIndex(e => new { e.TargetType, e.TargetId });
        builder.HasIndex(e => e.OccurredAt);
        builder.HasIndex(e => e.Action);
        builder.HasIndex(e => e.ActorUserId);
    }
}
