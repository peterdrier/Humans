using Humans.Feedback.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Feedback.Data.Configurations;

internal sealed class FeedbackReportConfiguration : IEntityTypeConfiguration<FeedbackReport>
{
    public void Configure(EntityTypeBuilder<FeedbackReport> builder)
    {
        builder.ToTable("feedback_reports");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.Category)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(f => f.Description)
            .HasMaxLength(5000)
            .IsRequired();

        builder.Property(f => f.PageUrl)
            .HasMaxLength(2000)
            .IsRequired();

        builder.Property(f => f.UserAgent)
            .HasMaxLength(1000);

        builder.Property(f => f.AdditionalContext)
            .HasMaxLength(2000);

        builder.Property(f => f.ScreenshotFileName)
            .HasMaxLength(256);

        builder.Property(f => f.ScreenshotStoragePath)
            .HasMaxLength(512);

        builder.Property(f => f.ScreenshotContentType)
            .HasMaxLength(64);

        builder.Property(f => f.Status)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        // Sentinel (FeedbackSource)(-1), not a real member: with the CLR default (UserReport == 0) as sentinel, EF
        // silently drops an explicit `Source = UserReport` in favor of the DB default. The DB default backfilled
        // existing rows.
        builder.Property(f => f.Source)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValueSql("'UserReport'")
            .HasSentinel((FeedbackSource)(-1))
            .IsRequired();

        builder.HasIndex(f => f.Source);
        builder.HasIndex(f => f.AgentConversationId);

        builder.Property(f => f.CreatedAt).IsRequired();
        builder.Property(f => f.UpdatedAt).IsRequired();

        builder.HasIndex(f => f.Status);
        builder.HasIndex(f => f.CreatedAt);
        builder.HasIndex(f => f.UserId);
        builder.HasIndex(f => f.AssignedToUserId);
        builder.HasIndex(f => f.AssignedToTeamId);
    }
}
