using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class LegalDocumentConfiguration : IEntityTypeConfiguration<LegalDocument>
{
    public void Configure(EntityTypeBuilder<LegalDocument> builder)
    {
        builder.ToTable("legal_documents");

        builder.HasKey(ld => ld.Id);

        builder.Property(ld => ld.Type)
            .IsRequired()
            .HasConversion<string>();

        builder.Property(ld => ld.Name)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(ld => ld.GitHubPath)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(ld => ld.CurrentCommitSha)
            .HasMaxLength(40);

        builder.Property(ld => ld.CreatedAt)
            .IsRequired();

        builder.Property(ld => ld.LastSyncedAt)
            .IsRequired();

        builder.HasMany(ld => ld.Versions)
            .WithOne(v => v.LegalDocument)
            .HasForeignKey(v => v.LegalDocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(ld => ld.Type);
        builder.HasIndex(ld => ld.IsActive);

        // Ignore computed property
        builder.Ignore(ld => ld.CurrentVersion);
    }
}
