using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Application.Architecture;

namespace Humans.Infrastructure.Data.Configurations.Issues;

[Grandfathered(
    ruleId: "HUM0024",
    justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.",
    since: "2026-05-25",
    issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]
public class IssueCommentConfiguration : IEntityTypeConfiguration<IssueComment>
{
    public void Configure(EntityTypeBuilder<IssueComment> b)
    {
        b.ToTable("issue_comments");
        b.HasKey(x => x.Id);

        b.Property(x => x.Content).HasMaxLength(5000).IsRequired();
        b.Property(x => x.CreatedAt).IsRequired();

        // Cross-section FK column only — no nav property (design-rules §6c,
        // memory/architecture/no-cross-section-ef-joins.md).
        b.HasOne<User>().WithMany().HasForeignKey(x => x.SenderUserId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasIndex(x => x.IssueId);
        b.HasIndex(x => x.CreatedAt);
    }
}
