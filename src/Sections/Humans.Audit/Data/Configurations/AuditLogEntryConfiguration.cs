using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Audit.Contracts;
using Humans.Audit.Domain;

namespace Humans.Audit.Data.Configurations;

/// <summary>Append-only: database triggers reject UPDATE and DELETE.</summary>
internal sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable("audit_log_entries");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Action)
            .HasMaxLength(AuditActionDefinition.NameMaxLength)
            .IsRequired();

        builder.Property(e => e.SubjectType)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(e => e.SubjectId)
            .IsRequired();

        builder.Property(e => e.Description)
            .IsRequired();

        builder.Property(e => e.OccurredAt)
            .IsRequired();

        builder.Property(e => e.TargetType)
            .HasMaxLength(100);

        builder.Property(e => e.JobName)
            .HasMaxLength(100);

        builder.Property(e => e.ErrorMessage)
            .HasMaxLength(4000);

        builder.Property(e => e.Role)
            .HasMaxLength(100);

        builder.Property(e => e.SyncSource)
            .HasConversion<string>()
            .HasMaxLength(100);

        builder.Property(e => e.UserEmail)
            .HasMaxLength(500);

        builder.HasIndex(e => new { e.SubjectType, e.SubjectId });
        builder.HasIndex(e => new { e.TargetType, e.TargetId });
        builder.HasIndex(e => e.OccurredAt);
        builder.HasIndex(e => e.Action);
        builder.HasIndex(e => e.ResourceId);
    }
}
