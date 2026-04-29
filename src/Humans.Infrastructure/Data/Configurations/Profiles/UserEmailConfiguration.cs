using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations.Profiles;

public class UserEmailConfiguration : IEntityTypeConfiguration<UserEmail>
{
    public void Configure(EntityTypeBuilder<UserEmail> builder)
    {
        builder.ToTable("user_emails");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Email)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(e => e.IsVerified)
            .IsRequired();

        builder.Property(e => e.IsNotificationTarget)
            .IsRequired();

        builder.Property(e => e.Visibility)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(e => e.CreatedAt)
            .IsRequired();

        builder.Property(e => e.UpdatedAt)
            .IsRequired();

        // PR 3 (additive): Provider / ProviderKey carry the OAuth identity tied
        // to this row; IsGoogle marks the canonical Workspace identity.
        // Single-row-per-(Provider, ProviderKey) and at-most-one-IsGoogle-true-
        // per-UserId are service-enforced inside UserEmailService — no DB
        // indexes per feedback_db_enforcement_minimal.
        builder.Property(e => e.Provider)
            .HasMaxLength(64);

        builder.Property(e => e.ProviderKey)
            .HasMaxLength(256);

        builder.Property(e => e.IsGoogle)
            .IsRequired();

        // PR 3: IsOAuth / DisplayOrder columns are kept on disk; their C#
        // properties are deleted in Task 10 of the plan, at which point these
        // mappings switch to shadow-property declarations
        // (Property<T>("Name").HasColumnName(...)) so the EF model still sees
        // the columns and the migration scaffolder doesn't generate a
        // DropColumn. Column drops happen in PR 7 after end-to-end prod
        // verification per architecture_no_drops_until_prod_verified.
        builder.Property(e => e.IsOAuth)
            .IsRequired();

        builder.Property(e => e.DisplayOrder)
            .IsRequired();

        builder.HasOne<User>()
            .WithMany(u => u.UserEmails)
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.UserId);

        // Unique index on verified emails (case-insensitive) to prevent email squatting
        builder.HasIndex(e => e.Email)
            .IsUnique()
            .HasFilter("\"IsVerified\" = true");
    }
}
