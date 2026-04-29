using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Issues;

public class IssueCommentConfiguration : IEntityTypeConfiguration<IssueComment>
{
    public void Configure(EntityTypeBuilder<IssueComment> b)
    {
        b.ToTable("issue_comments");
        b.HasKey(x => x.Id);

        b.Property(x => x.Content).HasMaxLength(5000).IsRequired();
        b.Property(x => x.CreatedAt).IsRequired();

        // EF needs the nav ref to configure the cross-section FK relationship.
        // The nav itself is [Obsolete] for Application callers; this block
        // owns the DB-level FK + cascade behavior.
#pragma warning disable CS0618
        b.HasOne(x => x.SenderUser).WithMany().HasForeignKey(x => x.SenderUserId)
            .OnDelete(DeleteBehavior.SetNull);
#pragma warning restore CS0618

        b.HasIndex(x => x.IssueId);
        b.HasIndex(x => x.CreatedAt);
    }
}
