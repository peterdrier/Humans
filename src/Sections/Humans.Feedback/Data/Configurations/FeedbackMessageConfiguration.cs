using Humans.Feedback.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Feedback.Data.Configurations;

internal sealed class FeedbackMessageConfiguration : IEntityTypeConfiguration<FeedbackMessage>
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

        // SenderUserId is a bare cross-section Guid column — no FK constraint, no nav.
        // Resolve senders via IUserServiceRead.

        builder.HasIndex(m => m.FeedbackReportId);
        builder.HasIndex(m => m.CreatedAt);
    }
}
