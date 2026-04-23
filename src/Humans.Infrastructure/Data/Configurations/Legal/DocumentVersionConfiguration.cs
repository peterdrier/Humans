using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations.Legal;

public class DocumentVersionConfiguration : IEntityTypeConfiguration<DocumentVersion>
{
    public void Configure(EntityTypeBuilder<DocumentVersion> builder)
    {
        builder.ToTable("document_versions");

        builder.HasKey(dv => dv.Id);

        builder.Property(dv => dv.VersionNumber)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(dv => dv.CommitSha)
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(dv => dv.Content)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null)
                     ?? new Dictionary<string, string>(StringComparer.Ordinal),
                new ValueComparer<Dictionary<string, string>>(
                    (a, b) => a != null && b != null && a.Count == b.Count && a.All(kv => b.ContainsKey(kv.Key) && string.Equals(kv.Value, b[kv.Key], StringComparison.Ordinal)),
                    v => v.Aggregate(0, (hash, kv) => HashCode.Combine(hash, StringComparer.Ordinal.GetHashCode(kv.Key), StringComparer.Ordinal.GetHashCode(kv.Value))),
                    v => new Dictionary<string, string>(v, StringComparer.Ordinal)));

        builder.Property(dv => dv.EffectiveFrom)
            .IsRequired();

        builder.Property(dv => dv.CreatedAt)
            .IsRequired();

        builder.Property(dv => dv.ChangesSummary)
            .HasMaxLength(2000);

        builder.HasMany(dv => dv.ConsentRecords)
            .WithOne(cr => cr.DocumentVersion)
            .HasForeignKey(cr => cr.DocumentVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(dv => dv.LegalDocumentId);
        builder.HasIndex(dv => dv.EffectiveFrom);
        builder.HasIndex(dv => dv.CommitSha);
    }
}
