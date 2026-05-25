using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Application.Architecture;

namespace Humans.Infrastructure.Data.Configurations.Feedback;

[Grandfathered(
    ruleId: "HUM0024",
    justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.",
    since: "2026-05-25",
    issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]
public class FeedbackMessageConfiguration : IEntityTypeConfiguration<FeedbackMessage>
{
    public void Configure(EntityTypeBuilder<FeedbackMessage> builder)
    {
        builder.ToTable("feedback_messages");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Content)
            .HasMaxLength(5000)
            .IsRequired();

        builder.Property(m => m.CreatedAt).IsRequired();

        builder.HasOne(m => m.FeedbackReport)
            .WithMany(r => r.Messages)
            .HasForeignKey(m => m.FeedbackReportId)
            .OnDelete(DeleteBehavior.Cascade);

        // EF needs the nav ref to configure the cross-section FK relationship.
        // The nav itself is [Obsolete] for Application callers; this block
        // owns the DB-level FK + cascade behavior.
#pragma warning disable CS0618
        builder.HasOne(m => m.SenderUser)
            .WithMany()
            .HasForeignKey(m => m.SenderUserId)
            .OnDelete(DeleteBehavior.SetNull);
#pragma warning restore CS0618

        builder.HasIndex(m => m.FeedbackReportId);
        builder.HasIndex(m => m.CreatedAt);
    }
}
