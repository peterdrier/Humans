using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Users.Contracts;

namespace Humans.Users.Data.Configurations;

internal sealed class UserEmailConfiguration : IEntityTypeConfiguration<UserEmail>
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

        // C# property renamed IsNotificationTarget → IsPrimary; DB column keeps
        // the legacy name per memory/architecture/no-drops-until-prod-verified.md.
        builder.Property(e => e.IsPrimary)
            .HasColumnName("IsNotificationTarget")
            .IsRequired();

        builder.Property(e => e.Visibility)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(e => e.CreatedAt)
            .IsRequired();

        builder.Property(e => e.UpdatedAt)
            .IsRequired();

        // Provider / ProviderKey carry the OAuth identity tied to this row;
        // IsGoogle marks the canonical Workspace identity.
        // Single-row-per-(Provider, ProviderKey) and at-most-one-IsGoogle-true-
        // per-UserId are service-enforced inside UserEmailService — no DB
        // indexes per feedback_db_enforcement_minimal.
        builder.Property(e => e.Provider)
            .HasMaxLength(64);

        builder.Property(e => e.ProviderKey)
            .HasMaxLength(256);

        builder.Property(e => e.IsGoogle)
            .IsRequired();

        // Per-address Google sync status. Mirrors the legacy User.GoogleEmailStatus
        // column mapping: string-converted enum, Unknown default/sentinel.
        builder.Property(e => e.GoogleEmailStatus)
            .HasConversion<string>()
            .HasMaxLength(50)
            .HasDefaultValue(GoogleEmailStatus.Unknown)
            .HasSentinel(GoogleEmailStatus.Unknown)
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

        // The "exactly one verified IsPrimary per user" invariant is service-
        // enforced inside UserEmailService.EnsurePrimaryInvariantAsync — no DB
        // partial unique index per memory/architecture/db-enforcement-minimal.md.
    }
}
