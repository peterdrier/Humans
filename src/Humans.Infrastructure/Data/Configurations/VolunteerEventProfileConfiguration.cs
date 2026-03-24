using System.Text.Json;
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class VolunteerEventProfileConfiguration : IEntityTypeConfiguration<VolunteerEventProfile>
{
    public void Configure(EntityTypeBuilder<VolunteerEventProfile> builder)
    {
        builder.ToTable("volunteer_event_profiles");
        builder.HasKey(v => v.Id);

        builder.HasIndex(v => v.UserId).IsUnique();

        var listComparer = new ValueComparer<List<string>>(
            (a, b) => a != null && b != null && a.SequenceEqual(b),
            v => v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
            v => v.ToList());

        ConfigureJsonbList(builder, v => v.Skills, listComparer);
        ConfigureJsonbList(builder, v => v.Quirks, listComparer);
        ConfigureJsonbList(builder, v => v.Languages, listComparer);
        ConfigureJsonbList(builder, v => v.Allergies, listComparer);
        ConfigureJsonbList(builder, v => v.Intolerances, listComparer);

        builder.Property(v => v.AllergyOtherText).HasMaxLength(500);
        builder.Property(v => v.IntoleranceOtherText).HasMaxLength(500);

        builder.Property(v => v.DietaryPreference).HasMaxLength(200);
        builder.Property(v => v.MedicalConditions).HasMaxLength(4000);

        builder.HasOne(v => v.User)
            .WithMany()
            .HasForeignKey(v => v.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureJsonbList(
        EntityTypeBuilder<VolunteerEventProfile> builder,
        System.Linq.Expressions.Expression<Func<VolunteerEventProfile, List<string>>> propertyExpression,
        ValueComparer<List<string>> comparer)
    {
        builder.Property(propertyExpression).HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new(),
                comparer);
    }
}
