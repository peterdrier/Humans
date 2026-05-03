using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Feedback;

public class FeedbackReportConfiguration : IEntityTypeConfiguration<FeedbackReport>
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

        // HasDefaultValueSql (raw SQL) instead of HasDefaultValue: the latter
        // trips EF's sentinel detection because FeedbackSource.UserReport == 0
        // is the CLR default and EF would silently overwrite explicit assignments
        // with the DB default. We still need a DB-level default so the migration
        // can backfill existing rows when this NOT-NULL column is added.
        builder.Property(f => f.Source)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValueSql("'UserReport'")
            .IsRequired();

        builder.HasIndex(f => f.Source);
        builder.HasIndex(f => f.AgentConversationId);

        builder.Property(f => f.CreatedAt).IsRequired();
        builder.Property(f => f.UpdatedAt).IsRequired();

        // EF needs the nav refs to configure the cross-section FK relationships.
        // The nav properties themselves are [Obsolete] for the Application layer,
        // but the DB-level FK + cascade behavior is still owned here — suppress
        // the obsolete warning only for this wiring block.
        // No cross-section FK to agent_conversations. AgentConversationId is
        // a plain nullable Guid column on feedback_reports — Feedback owns the
        // column, Agent owns the referenced rows, and EF does not model the
        // join. Index on the column lives below.

#pragma warning disable CS0618
        builder.HasOne(f => f.User)
            .WithMany()
            .HasForeignKey(f => f.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(f => f.ResolvedByUser)
            .WithMany()
            .HasForeignKey(f => f.ResolvedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(f => f.AssignedToUser)
            .WithMany()
            .HasForeignKey(f => f.AssignedToUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(f => f.AssignedToTeam)
            .WithMany()
            .HasForeignKey(f => f.AssignedToTeamId)
            .OnDelete(DeleteBehavior.SetNull);
#pragma warning restore CS0618

        builder.HasIndex(f => f.Status);
        builder.HasIndex(f => f.CreatedAt);
        builder.HasIndex(f => f.UserId);
        builder.HasIndex(f => f.AssignedToUserId);
        builder.HasIndex(f => f.AssignedToTeamId);
    }
}
