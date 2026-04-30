using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Issues;

public class IssueConfiguration : IEntityTypeConfiguration<Issue>
{
    public void Configure(EntityTypeBuilder<Issue> b)
    {
        b.ToTable("issues");
        b.HasKey(x => x.Id);

        b.Property(x => x.ReporterUserId).IsRequired();
        b.Property(x => x.Section).HasMaxLength(64);
        b.Property(x => x.Category).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(5000).IsRequired();
        b.Property(x => x.PageUrl).HasMaxLength(2000);
        b.Property(x => x.UserAgent).HasMaxLength(1000);
        b.Property(x => x.AdditionalContext).HasMaxLength(2000);
        b.Property(x => x.ScreenshotFileName).HasMaxLength(256);
        b.Property(x => x.ScreenshotStoragePath).HasMaxLength(512);
        b.Property(x => x.ScreenshotContentType).HasMaxLength(64);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(x => x.GitHubIssueNumber);
        b.Property(x => x.DueDate);

        b.Property(x => x.CreatedAt).IsRequired();
        b.Property(x => x.UpdatedAt).IsRequired();

        // EF needs the nav refs to configure the cross-section FK relationships.
        // The nav properties themselves are [Obsolete] for the Application layer,
        // but the DB-level FK behavior is still owned here — suppress the
        // obsolete warning only for this wiring block.
        //
        // Reporter FK uses Restrict: account deletion in this codebase
        // anonymizes the User row in place (see IAccountDeletionService) — the
        // row is never physically removed, so the FK should never trip. If
        // anyone ever bypasses the deletion service and tries to Remove() a
        // user, Restrict makes the DB reject it instead of silently wiping
        // every issue the user reported.
#pragma warning disable CS0618
        b.HasOne(x => x.Reporter).WithMany().HasForeignKey(x => x.ReporterUserId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.Assignee).WithMany().HasForeignKey(x => x.AssigneeUserId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasOne(x => x.ResolvedByUser).WithMany().HasForeignKey(x => x.ResolvedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
#pragma warning restore CS0618

        b.HasMany(x => x.Comments).WithOne(c => c.Issue).HasForeignKey(c => c.IssueId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.Status);
        b.HasIndex(x => x.CreatedAt);
        b.HasIndex(x => x.ReporterUserId);
        b.HasIndex(x => x.AssigneeUserId);
        b.HasIndex(x => x.Section);
        b.HasIndex(x => new { x.Section, x.Status });
    }
}
