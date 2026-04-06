using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Data.Configurations;

public class ProfileLanguageConfiguration : IEntityTypeConfiguration<ProfileLanguage>
{
    public void Configure(EntityTypeBuilder<ProfileLanguage> builder)
    {
        builder.ToTable("profile_languages");

        builder.HasKey(pl => pl.Id);

        builder.Property(pl => pl.LanguageCode)
            .IsRequired()
            .HasMaxLength(10);

        builder.Property(pl => pl.Proficiency)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.HasOne(pl => pl.Profile)
            .WithMany(p => p.Languages)
            .HasForeignKey(pl => pl.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(pl => pl.ProfileId);
    }
}
