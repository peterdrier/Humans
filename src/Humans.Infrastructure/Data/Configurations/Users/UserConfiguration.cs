using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Data.Configurations.Users;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.Property(u => u.DisplayName)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(u => u.PreferredLanguage)
            .HasMaxLength(10)
            .HasDefaultValue("en");

        builder.Property(u => u.ProfilePictureUrl)
            .HasMaxLength(2048);

        // GoogleEmail column is kept on disk as an EF shadow property; the C#
        // property has been deleted. Column drop happens in a deferred PR
        // after end-to-end prod verification per
        // architecture_no_drops_until_prod_verified.
        builder.Property<string?>("GoogleEmail")
            .HasColumnName("GoogleEmail")
            .HasMaxLength(256);

        builder.Property(u => u.GoogleEmailStatus)
            .HasConversion<string>()
            .HasMaxLength(50)
            .HasDefaultValue(GoogleEmailStatus.Unknown)
            .HasSentinel(GoogleEmailStatus.Unknown)
            .IsRequired();

        builder.Property(u => u.CreatedAt)
            .IsRequired();

        // Issue #635 (§15i): the User-side cross-domain navs (Profile,
        // RoleAssignments, ConsentRecords, Applications, TeamMemberships,
        // CommunicationPreferences) are stripped. Their FK constraints are
        // preserved by inverse-side configurations on each owning entity's
        // *Configuration class — see e.g. ProfileConfiguration,
        // RoleAssignmentConfiguration, ConsentRecordConfiguration. The
        // UserEmails nav stays (User.Email override depends on it) and is
        // configured below.
        builder.HasMany(u => u.UserEmails)
            .WithOne()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(u => u.Email);

        // Contact import fields
        builder.Property(u => u.ContactSource)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(u => u.ExternalSourceId)
            .HasMaxLength(256);

        builder.HasIndex(u => new { u.ContactSource, u.ExternalSourceId })
            .HasFilter("\"ExternalSourceId\" IS NOT NULL");

        // Account-merge tombstone marker. Self-referential FK with no cascade
        // — deleting the target must not cascade-delete the source tombstone.
        // Filtered index because the column is null for live users.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(u => u.MergedToUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(u => u.MergedToUserId)
            .HasFilter("\"MergedToUserId\" IS NOT NULL");

        // Ignore GetEffectiveEmail (method, not property - EF won't map it, but defensive)
    }
}
