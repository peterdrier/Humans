using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Consent.Domain;

namespace Humans.Consent.Data;

/// <summary>Append-only: a trigger in the section baseline migration rejects UPDATE and DELETE.</summary>
internal sealed class ConsentRecordConfiguration : IEntityTypeConfiguration<ConsentRecord>
{
    public void Configure(EntityTypeBuilder<ConsentRecord> builder)
    {
        builder.ToTable("consent_records");

        builder.HasKey(cr => cr.Id);

        builder.Property(cr => cr.ConsentedAt)
            .IsRequired();

        builder.Property(cr => cr.IpAddress)
            .HasMaxLength(45) // IPv6
            .IsRequired();

        builder.Property(cr => cr.UserAgent)
            .HasMaxLength(1024)
            .IsRequired();

        builder.Property(cr => cr.ContentHash)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(cr => cr.ExplicitConsent)
            .IsRequired();

        builder.HasIndex(cr => new { cr.UserId, cr.DocumentVersionId })
            .IsUnique();

        builder.HasIndex(cr => cr.UserId);
        builder.HasIndex(cr => cr.DocumentVersionId);
        builder.HasIndex(cr => cr.ConsentedAt);
        builder.HasIndex(cr => new { cr.UserId, cr.ExplicitConsent, cr.ConsentedAt });
    }
}
