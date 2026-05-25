using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Application.Architecture;

namespace Humans.Infrastructure.Data.Configurations.Feedback;

[Grandfathered(
    ruleId: "HUM0024",
    justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.",
    since: "2026-05-25",
    issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]
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

        // Sentinel = (FeedbackSource)(-1) — not a real enum member — so EF can
        // distinguish "explicitly assigned" from "unset". Without it the CLR
        // default (UserReport == 0) tripped EF's sentinel detection: explicit
        // `Source = FeedbackSource.UserReport` assignments were silently
        // dropped in favor of the DB default. Behavior was accidentally
        // correct only because the DB default and CLR default both produce
        // 'UserReport' today. Keeps the DB default for migration backfill of
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
