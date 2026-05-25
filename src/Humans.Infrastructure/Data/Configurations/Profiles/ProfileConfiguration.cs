using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Application.Architecture;

namespace Humans.Infrastructure.Data.Configurations.Profiles;

[Grandfathered(
    ruleId: "HUM0024",
    justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.",
    since: "2026-05-25",
    issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]
public class ProfileConfiguration : IEntityTypeConfiguration<Profile>
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

        builder.Property(p => p.IsApproved)
            .IsRequired();

        builder.Property(p => p.MembershipTier)
            .IsRequired()
            .HasDefaultValue(MembershipTier.Volunteer)
            .HasConversion<string>();

        // Issue #635 (§15i): nullable string column. Existing rows hold NULL
        // until CachingUserService lazily computes and writes back. A
        // follow-up PR promotes to NOT NULL after every row is populated.
        builder.Property(p => p.State)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(p => p.ConsentCheckStatus)
            .HasConversion<string>();

        builder.Property(p => p.ConsentCheckNotes)
            .HasMaxLength(4000);

        builder.Property(p => p.RejectionReason)
            .HasMaxLength(4000);

        builder.Property(p => p.DateOfBirth);

        builder.Property(p => p.EmergencyContactName)
            .HasMaxLength(256);

        builder.Property(p => p.EmergencyContactPhone)
            .HasMaxLength(50);

        builder.Property(p => p.EmergencyContactRelationship)
            .HasMaxLength(100);

        // Dietary + medical (moved from VolunteerEventProfile). Lists persisted as
        // JSONB with a sequence-equality value comparer, mirroring the prior VEP map.
        var listComparer = new ValueComparer<List<string>>(
            (a, b) => a != null && b != null && a.SequenceEqual(b),
            v => v.Aggregate(0, HashCode.Combine),
            v => v.ToList());

        ConfigureJsonbList(builder, p => p.Allergies, listComparer);
        ConfigureJsonbList(builder, p => p.Intolerances, listComparer);

        builder.Property(p => p.DietaryPreference).HasMaxLength(200);
        builder.Property(p => p.AllergyOtherText).HasMaxLength(500);
        builder.Property(p => p.IntoleranceOtherText).HasMaxLength(500);
        builder.Property(p => p.MedicalConditions).HasMaxLength(4000);

        builder.Property(p => p.NoPriorBurnExperience)
            .IsRequired();

        builder.Property(p => p.Iban)
            .HasMaxLength(34);

        builder.Property(p => p.ProfilePictureContentType)
            .HasMaxLength(100);

        builder.Property(p => p.CreatedAt)
            .IsRequired();

        builder.Property(p => p.UpdatedAt)
            .IsRequired();

        builder.HasIndex(p => p.UserId)
            .IsUnique();

        // Issue #635 (§15i): inverse-side FK preservation after the User-side
        // nav (User.Profile) was stripped. Configures the schema-level FK +
        // cascade-delete that previously lived on UserConfiguration.HasOne.
        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<Profile>(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => p.ConsentCheckStatus);

        // Ignore computed properties
        builder.Ignore(p => p.FullName);
        builder.Ignore(p => p.HasCustomProfilePicture);
    }

    private static void ConfigureJsonbList(
        EntityTypeBuilder<Profile> builder,
        System.Linq.Expressions.Expression<Func<Profile, List<string>>> propertyExpression,
        ValueComparer<List<string>> comparer)
    {
        builder.Property(propertyExpression).HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new(),
                comparer);
    }
}
