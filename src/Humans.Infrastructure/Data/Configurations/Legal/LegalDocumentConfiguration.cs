using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Application.Architecture;

namespace Humans.Infrastructure.Data.Configurations.Legal;

[Grandfathered(
    ruleId: "HUM0024",
    justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.",
    since: "2026-05-25",
    issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]
public class LegalDocumentConfiguration : IEntityTypeConfiguration<LegalDocument>
{
    public void Configure(EntityTypeBuilder<LegalDocument> builder)
    {
        builder.ToTable("legal_documents");

        builder.HasKey(ld => ld.Id);

        builder.Property(ld => ld.Name)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(ld => ld.TeamId)
            .IsRequired();

        builder.Property(ld => ld.GracePeriodDays)
            .IsRequired()
            .HasDefaultValue(7);

        builder.Property(ld => ld.GitHubFolderPath)
            .HasMaxLength(512);

        builder.Property(ld => ld.CurrentCommitSha)
            .HasMaxLength(40);

        builder.Property(ld => ld.CreatedAt)
            .IsRequired();

        builder.Property(ld => ld.LastSyncedAt)
            .IsRequired();

        builder.HasOne<Team>()
            .WithMany()
            .HasForeignKey(ld => ld.TeamId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(ld => ld.Versions)
            .WithOne(v => v.LegalDocument)
            .HasForeignKey(v => v.LegalDocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(ld => ld.IsActive);
        builder.HasIndex(ld => new { ld.TeamId, ld.IsActive });

        // Ignore computed property
        builder.Ignore(ld => ld.CurrentVersion);
    }
}
