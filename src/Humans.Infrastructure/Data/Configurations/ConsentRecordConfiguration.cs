using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

/// <summary>
/// Configuration for ConsentRecord entity.
/// This table is append-only - no updates or deletes should be performed.
/// A database trigger should be created to enforce this at the database level.
/// </summary>
public class ConsentRecordConfiguration : IEntityTypeConfiguration<ConsentRecord>
{
    public void Configure(EntityTypeBuilder<ConsentRecord> builder)
    {
        builder.ToTable("consent_records");

        builder.HasKey(cr => cr.Id);

        builder.Property(cr => cr.ConsentedAt)
            .IsRequired();

        builder.Property(cr => cr.IpAddress)
            .HasMaxLength(45) // IPv6 max length
            .IsRequired();

        builder.Property(cr => cr.UserAgent)
            .HasMaxLength(1024)
            .IsRequired();

        builder.Property(cr => cr.ContentHash)
            .HasMaxLength(64) // SHA-256 hex
            .IsRequired();

        builder.Property(cr => cr.ExplicitConsent)
            .IsRequired();

        // Create unique index to prevent duplicate consents for same user/version
        builder.HasIndex(cr => new { cr.UserId, cr.DocumentVersionId })
            .IsUnique();

        builder.HasIndex(cr => cr.UserId);
        builder.HasIndex(cr => cr.DocumentVersionId);
        builder.HasIndex(cr => cr.ConsentedAt);
        builder.HasIndex(cr => new { cr.UserId, cr.ExplicitConsent, cr.ConsentedAt });
    }
}
