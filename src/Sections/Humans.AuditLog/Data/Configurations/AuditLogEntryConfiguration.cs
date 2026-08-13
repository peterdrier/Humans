using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.AuditLog.Domain;

namespace Humans.AuditLog.Data.Configurations;

/// <summary>
/// Configuration for AuditLogEntry entity.
/// This table is append-only — no updates or deletes should be performed.
/// A database trigger enforces this at the database level.
/// </summary>
internal sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable("audit_log");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Action)
            .HasConversion<string>()
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(e => e.EntityType)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(e => e.EntityId)
            .IsRequired();

        builder.Property(e => e.Description)
            .HasMaxLength(4000)
            .IsRequired();

        builder.Property(e => e.OccurredAt)
            .IsRequired();

        builder.Property(e => e.RelatedEntityType)
            .HasMaxLength(100);

        // ActorUserId is a bare cross-section Guid column — no FK constraint, no nav
        // (null = system action).

        // Google sync-specific fields (all nullable)
        builder.Property(e => e.ErrorMessage)
            .HasMaxLength(4000);

        builder.Property(e => e.Role)
            .HasMaxLength(100);

        builder.Property(e => e.SyncSource)
            .HasConversion<string>()
            .HasMaxLength(100);

        builder.Property(e => e.UserEmail)
            .HasMaxLength(500);

        // ResourceId is a bare cross-section Guid column — no FK constraint, no nav.

        // Indexes for querying
        builder.HasIndex(e => new { e.EntityType, e.EntityId });
        builder.HasIndex(e => new { e.RelatedEntityType, e.RelatedEntityId });
        builder.HasIndex(e => e.OccurredAt);
        builder.HasIndex(e => e.Action);
        builder.HasIndex(e => e.ResourceId);
    }
}
