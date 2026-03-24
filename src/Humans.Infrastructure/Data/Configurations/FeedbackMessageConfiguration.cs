using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

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

        builder.HasOne(m => m.SenderUser)
            .WithMany()
            .HasForeignKey(m => m.SenderUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(m => m.FeedbackReportId);
        builder.HasIndex(m => m.CreatedAt);
    }
}
