using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Users.Contracts;

namespace Humans.Users.Data.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.Property<string>("DisplayName")
            .HasMaxLength(256)
            .IsRequired();

        // Nullable until the admin backfill runs.
        builder.Property(u => u.BurnerName)
            .HasMaxLength(256);

        builder.Property(u => u.FirstName)
            .HasMaxLength(256);

        builder.Property(u => u.LastName)
            .HasMaxLength(256);

        builder.Property(u => u.PreferredLanguage)
            .HasMaxLength(10)
            .HasDefaultValue("en");

        builder.Property(u => u.ProfilePictureUrl)
            .HasMaxLength(2048);

        // Shadow property nothing reads or writes; mapped so EF keeps the column until its drop.
        builder.Property<string?>("GoogleEmail")
            .HasColumnName("GoogleEmail")
            .HasMaxLength(256);

        // Shadow property nothing reads or writes; mapped so EF keeps the column until its drop.
        builder.Property<LegacyGoogleEmailStatus>("GoogleEmailStatus")
            .HasConversion<string>()
            .HasMaxLength(50)
            .HasDefaultValue(LegacyGoogleEmailStatus.Unknown)
            .HasSentinel(LegacyGoogleEmailStatus.Unknown)
            .IsRequired();

        builder.Property(u => u.CreatedAt)
            .IsRequired();

        // The User.Email override depends on this nav.
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

        // The physical default keeps model and database agreed (EF would scaffold '', not a
        // parseable UserState); it is out of range so an explicit Bare (== 0) never reads as unset.
        builder.Property(u => u.State)
            .HasConversion<string>()
            .HasMaxLength(50)
            .HasDefaultValue(UserState.Bare)
            .HasSentinel((UserState)(-1))
            .IsRequired();
    }
}
