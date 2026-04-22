using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

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

        builder.Property(f => f.CreatedAt).IsRequired();
        builder.Property(f => f.UpdatedAt).IsRequired();

        // EF needs the nav refs to configure the cross-section FK relationships.
        // The nav properties themselves are [Obsolete] for the Application layer,
        // but the DB-level FK + cascade behavior is still owned here — suppress
        // the obsolete warning only for this wiring block.
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
