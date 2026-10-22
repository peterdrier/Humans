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

        // The column keeps its legacy name IsNotificationTarget.
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

        // One row per (Provider, ProviderKey) is enforced in UserEmailService, not by an index.
        // IsGoogle / GoogleEmailStatus are frozen (GoogleIntegration owns the Workspace identity)
        // and stay mapped until their drop PR.
        builder.Property(e => e.Provider)
            .HasMaxLength(64);

        builder.Property(e => e.ProviderKey)
            .HasMaxLength(256);

        builder.Property(e => e.IsGoogle)
            .IsRequired();

        // Frozen per-address Google sync status: string-converted enum, Unknown default/sentinel.
        builder.Property(e => e.GoogleEmailStatus)
            .HasConversion<string>()
            .HasMaxLength(50)
            .HasDefaultValue(LegacyGoogleEmailStatus.Unknown)
            .HasSentinel(LegacyGoogleEmailStatus.Unknown)
            .IsRequired();

        builder.HasOne<User>()
            .WithMany(u => u.UserEmails)
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.UserId);

        // Case-insensitive unique index on verified emails prevents email squatting.
        builder.HasIndex(e => e.Email)
            .IsUnique()
            .HasFilter("\"IsVerified\" = true");

        // One verified IsPrimary per user is enforced in
        // UserEmailService.EnsurePrimaryInvariantAsync, not by a partial unique index.
    }
}
