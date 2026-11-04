using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Users.Contracts;

namespace Humans.Users.Data.Configurations;

internal sealed class ProfileConfiguration : IEntityTypeConfiguration<Profile>
{
    public void Configure(EntityTypeBuilder<Profile> builder)
    {
        builder.ToTable("profiles");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.BurnerName)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(p => p.FirstName)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(p => p.LastName)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(p => p.City)
            .HasMaxLength(256);

        builder.Property(p => p.CountryCode)
            .HasMaxLength(2);

        builder.Property(p => p.PlaceId)
            .HasMaxLength(512);

        builder.Property(p => p.Bio)
            .HasMaxLength(4000);

        builder.Property(p => p.Pronouns)
            .HasMaxLength(100);

        builder.Property(p => p.AdminNotes)
            .HasMaxLength(4000);

        builder.Property(p => p.DateOfBirth);

        builder.Property(p => p.EmergencyContactName)
            .HasMaxLength(256);

        builder.Property(p => p.EmergencyContactPhone)
            .HasMaxLength(50);

        builder.Property(p => p.EmergencyContactRelationship)
            .HasMaxLength(100);

        builder.Property(p => p.MedicalConditions).HasMaxLength(4000);

        builder.Property(p => p.NoPriorBurnExperience)
            .IsRequired();

        builder.Property(p => p.ProfilePictureContentType)
            .HasMaxLength(100);

        builder.Property(p => p.ProfilePictureStorageKey)
            .HasMaxLength(300);

        builder.Property(p => p.CreatedAt)
            .IsRequired();

        builder.Property(p => p.UpdatedAt)
            .IsRequired();

        builder.HasIndex(p => p.UserId)
            .IsUnique();

        // This config owns the cascade-delete FK from User to Profile.
        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<Profile>(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Ignore(p => p.FullName);
        builder.Ignore(p => p.HasCustomProfilePicture);
    }
}
