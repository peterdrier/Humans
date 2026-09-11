using System.Text.Json;
using Humans.Workgroups.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Workgroups.Data.Configurations;

internal sealed class WorkgroupDocumentConfiguration : IEntityTypeConfiguration<WorkgroupDocument>
{
    public void Configure(EntityTypeBuilder<WorkgroupDocument> b)
    {
        b.ToTable("workgroup_documents");
        b.HasKey(d => d.Id);

        b.Property(d => d.WorkgroupId).IsRequired();
        b.Property(d => d.Title).HasMaxLength(200).IsRequired();
        b.Property(d => d.Kind).IsRequired().HasConversion<string>().HasMaxLength(50);
        b.Property(d => d.Body).HasColumnType("text").IsRequired();
        b.Property(d => d.Status).IsRequired().HasConversion<string>().HasMaxLength(50);

        // Comment categories as jsonb (List<string>) — mirrors SurveyAnswerConfiguration.
        b.Property(d => d.CommentCategories).HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, WorkgroupsJson.Options),
                v => JsonSerializer.Deserialize<List<string>>(v, WorkgroupsJson.Options) ?? new(),
                new ValueComparer<List<string>>(
                    (a, c) => a != null && c != null && a.SequenceEqual(c),
                    v => v.Aggregate(0, HashCode.Combine),
                    v => v.ToList()));

        b.Property(d => d.Disposition).HasConversion<string>().HasMaxLength(50);
        b.Property(d => d.DispositionNote).HasMaxLength(4000);
        // DispositionByUserId, CreatedByUserId and UpdatedByUserId are bare cross-section
        // references: no FK, no navigation.
        b.Property(d => d.CreatedAt).IsRequired();
        b.Property(d => d.UpdatedAt).IsRequired();

        b.HasOne(d => d.Workgroup)
            .WithMany(w => w.Documents)
            .HasForeignKey(d => d.WorkgroupId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(d => new { d.WorkgroupId, d.Status });
        b.HasIndex(d => d.CreatedByUserId).HasFilter("\"CreatedByUserId\" IS NOT NULL");
    }
}
