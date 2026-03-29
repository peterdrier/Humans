using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Data.Configurations;

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

        builder.Property(u => u.CreatedAt)
            .IsRequired();

        builder.HasOne(u => u.Profile)
            .WithOne(p => p.User)
            .HasForeignKey<Profile>(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(u => u.RoleAssignments)
            .WithOne(ra => ra.User)
            .HasForeignKey(ra => ra.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(u => u.ConsentRecords)
            .WithOne(cr => cr.User)
            .HasForeignKey(cr => cr.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(u => u.Applications)
            .WithOne(a => a.User)
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(u => u.TeamMemberships)
            .WithOne(tm => tm.User)
            .HasForeignKey(tm => tm.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(u => u.UserEmails)
            .WithOne(e => e.User)
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(u => u.CommunicationPreferences)
            .WithOne(cp => cp.User)
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
