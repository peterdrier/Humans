using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations.Legal;

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

#pragma warning disable CS0618 // LegalDocument.Team is an obsolete cross-domain nav (peterdrier#719); EF config still references it so the snapshot stays in sync until a follow-up PR strips both.
        builder.HasOne(ld => ld.Team)
            .WithMany(t => t.LegalDocuments)
            .HasForeignKey(ld => ld.TeamId)
            .OnDelete(DeleteBehavior.Restrict);
#pragma warning restore CS0618

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
