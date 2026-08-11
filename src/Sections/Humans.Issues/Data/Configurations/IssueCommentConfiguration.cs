using Humans.Issues.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Issues.Data.Configurations;

internal sealed class IssueCommentConfiguration : IEntityTypeConfiguration<IssueComment>
{
    public void Configure(EntityTypeBuilder<IssueComment> b)
    {
        b.ToTable("issue_comments");
        b.HasKey(x => x.Id);

        b.Property(x => x.Content).HasMaxLength(5000).IsRequired();
        b.Property(x => x.CreatedAt).IsRequired();

        // SenderUserId is a bare cross-section Guid column — no FK constraint, no nav
        // (memory/architecture/no-cross-section-ef-joins.md).

        b.HasIndex(x => x.IssueId);
        b.HasIndex(x => x.CreatedAt);
    }
}
