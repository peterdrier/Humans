using Humans.Consent.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Consent.Data;

internal sealed class ConsentReviewConfiguration : IEntityTypeConfiguration<ConsentReview>
{
    public void Configure(EntityTypeBuilder<ConsentReview> builder)
    {
        builder.ToTable("consent_reviews");

        builder.HasKey(r => r.UserId);
        builder.Property(r => r.UserId).ValueGeneratedNever();

        builder.Property(r => r.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(r => r.Notes)
            .HasMaxLength(4000);

        builder.Property(r => r.IsApproved)
            .IsRequired();

        builder.Property(r => r.RejectionReason)
            .HasMaxLength(4000);

        builder.Property(r => r.UpdatedAt)
            .IsRequired();
    }
}
