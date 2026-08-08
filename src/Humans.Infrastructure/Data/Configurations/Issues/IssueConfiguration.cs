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

        // ReporterUserId / AssigneeUserId / ResolvedByUserId are bare cross-section
        // Guid columns — no FK constraint, no nav (memory/architecture/no-cross-section-ef-joins.md).
        // Resolve display data via IUserService; repositories must not .Include()
        // across sections. Account deletion anonymises the User row in place
        // (IAccountDeletionService), so these ids stay resolvable.

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
