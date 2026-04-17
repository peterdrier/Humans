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

        builder.Property(u => u.GoogleEmailStatus)
            .HasConversion<string>()
            .HasMaxLength(50)
            .HasDefaultValue(GoogleEmailStatus.Unknown)
            .HasSentinel(GoogleEmailStatus.Unknown)
            .IsRequired();

        builder.Property(u => u.CreatedAt)
            .IsRequired();

        // Cross-domain navs from User have been stripped (Phase C3 of User
        // section migration). Relationships are now configured from the
        // other side in their respective *Configuration.cs files, or
        // inferred by EF from the UserId FK convention.

        builder.HasIndex(u => u.Email);

        // Contact import fields
        builder.Property(u => u.ContactSource)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(u => u.ExternalSourceId)
            .HasMaxLength(256);

        builder.HasIndex(u => new { u.ContactSource, u.ExternalSourceId })
            .HasFilter("\"ExternalSourceId\" IS NOT NULL");
    }
}
