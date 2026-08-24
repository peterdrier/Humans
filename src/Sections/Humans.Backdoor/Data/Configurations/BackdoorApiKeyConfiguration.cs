using Humans.Backdoor.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Backdoor.Data.Configurations;

internal sealed class BackdoorApiKeyConfiguration : IEntityTypeConfiguration<BackdoorApiKey>
{
    public void Configure(EntityTypeBuilder<BackdoorApiKey> builder)
    {
        builder.ToTable("backdoor_api_keys");
        builder.HasKey(k => k.Id);

        // 64 hex characters — SHA-256. Unique because validation looks a presented key up
        // by its hash and must find at most one row.
        builder.Property(k => k.KeyHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(k => k.KeyHash).IsUnique();

        builder.Property(k => k.DisplayPrefix).HasMaxLength(16).IsRequired();
        builder.Property(k => k.Label).HasMaxLength(100).IsRequired();

        builder.Property(k => k.CreatedAt).IsRequired();

        // UserId / CreatedByUserId / RevokedByUserId are bare cross-section Guid columns —
        // no FK constraint, no nav (memory/architecture/no-cross-section-ef-joins.md).

        builder.HasIndex(k => k.UserId);

        // Computed from RevokedAt — not a column.
        builder.Ignore(k => k.IsActive);
    }
}
