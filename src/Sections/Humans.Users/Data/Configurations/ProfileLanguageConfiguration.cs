using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Users.Contracts;

namespace Humans.Users.Data.Configurations;

internal sealed class ProfileLanguageConfiguration : IEntityTypeConfiguration<ProfileLanguage>
{
    public void Configure(EntityTypeBuilder<ProfileLanguage> builder)
    {
        builder.ToTable("user_profile_languages");

        builder.HasKey(pl => pl.Id);

        builder.Property(pl => pl.LanguageCode)
            .IsRequired()
            .HasMaxLength(10);

        builder.Property(pl => pl.Proficiency)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(pl => pl.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Declared so the model matches the default EF's nullable-to-required migration leaves in the
        // database (required-columns-need-approval). Every write sets a real user id.
        builder.Property(pl => pl.UserId).HasDefaultValue(Guid.Empty);

        builder.HasIndex(pl => pl.UserId);
    }
}
