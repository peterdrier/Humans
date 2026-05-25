using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Data.Configurations.Users;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.Property<string>("DisplayName")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(u => u.PreferredLanguage)
            .HasMaxLength(10)
            .HasDefaultValue("en");

        builder.Property(u => u.ProfilePictureUrl)
            .HasMaxLength(2048);

        // Shadow property — column drop deferred until prod soak (no-drops-until-prod-verified).
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

        // UserEmails nav stays (User.Email override depends on it). Other cross-domain FKs are inverse-side. See #635.
        builder.HasMany(u => u.UserEmails)
            .WithOne()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(u => u.Email);

        builder.Property(u => u.ContactSource)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(u => u.ExternalSourceId)
            .HasMaxLength(256);

        builder.HasIndex(u => new { u.ContactSource, u.ExternalSourceId })
            .HasFilter("\"ExternalSourceId\" IS NOT NULL");

        // Merge tombstone: self-FK with Restrict — target delete must not cascade-delete the source.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(u => u.MergedToUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(u => u.MergedToUserId)
            .HasFilter("\"MergedToUserId\" IS NOT NULL");
    }
}
