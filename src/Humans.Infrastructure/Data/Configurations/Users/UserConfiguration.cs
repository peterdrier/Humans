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

        builder.Property(u => u.GoogleEmail)
            .HasMaxLength(256);

        builder.Property(u => u.GoogleEmailStatus)
            .HasConversion<string>()
            .HasMaxLength(50)
            .HasDefaultValue(GoogleEmailStatus.Unknown)
            .HasSentinel(GoogleEmailStatus.Unknown)
            .IsRequired();

        builder.Property(u => u.CreatedAt)
            .IsRequired();

        builder.HasOne(u => u.Profile)
            .WithOne()
            .HasForeignKey<Profile>(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // EF needs the nav ref to configure the cross-section FK relationship.
        // RoleAssignment.User is [Obsolete] for Application callers.
#pragma warning disable CS0618
        builder.HasMany(u => u.RoleAssignments)
            .WithOne(ra => ra.User)
            .HasForeignKey(ra => ra.UserId)
            .OnDelete(DeleteBehavior.Cascade);
#pragma warning restore CS0618

        builder.HasMany(u => u.ConsentRecords)
            .WithOne(cr => cr.User)
            .HasForeignKey(cr => cr.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // One-way relationship: User.Applications collection nav is
        // preserved (still used by ProfileService's admin flow — tracked as
        // a known incoming violation in Governance.md that the Profile
        // migration will clean up) but the back-nav Application.User has
        // been stripped per §6. EF configures the relationship without a
        // back-reference expression.
        builder.HasMany(u => u.Applications)
            .WithOne()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

#pragma warning disable CS0618 // TeamMember.User is Obsolete per §6c; kept for EF FK + inverse nav.
        builder.HasMany(u => u.TeamMemberships)
            .WithOne(tm => tm.User)
            .HasForeignKey(tm => tm.UserId)
            .OnDelete(DeleteBehavior.Cascade);
#pragma warning restore CS0618

        builder.HasMany(u => u.UserEmails)
            .WithOne()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(u => u.CommunicationPreferences)
            .WithOne()
            .HasForeignKey(cp => cp.UserId)
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

        // Ignore GetEffectiveEmail (method, not property - EF won't map it, but defensive)
    }
}
