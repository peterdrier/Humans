using Humans.Workgroups.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Workgroups.Data.Configurations;

internal sealed class WorkgroupDocumentCommentConfiguration : IEntityTypeConfiguration<WorkgroupDocumentComment>
{
    public void Configure(EntityTypeBuilder<WorkgroupDocumentComment> b)
    {
        b.ToTable("workgroup_document_comments");
        b.HasKey(c => c.Id);

        b.Property(c => c.DocumentId).IsRequired();
        b.Property(c => c.Category).HasMaxLength(100).IsRequired();
        // AuthorUserId, RespondedByUserId and HiddenByUserId are bare cross-section
        // references: no FK, no navigation. AuthorUserId is nulled on erasure; the body stays.
        b.Property(c => c.Body).HasMaxLength(4000).IsRequired();
        b.Property(c => c.CreatedAt).IsRequired();
        b.Property(c => c.Disposition).IsRequired().HasConversion<string>().HasMaxLength(50);
        b.Property(c => c.Response).HasMaxLength(4000);
        b.Property(c => c.HiddenReason).HasMaxLength(500);

        b.HasOne(c => c.Document)
            .WithMany(d => d.Comments)
            .HasForeignKey(c => c.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(c => new { c.DocumentId, c.Category });
        b.HasIndex(c => c.AuthorUserId).HasFilter("\"AuthorUserId\" IS NOT NULL");
    }
}
