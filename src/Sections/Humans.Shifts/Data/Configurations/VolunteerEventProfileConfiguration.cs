using System.Text.Json;
using Humans.Shifts.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Shifts.Data.Configurations;

internal sealed class VolunteerEventProfileConfiguration : IEntityTypeConfiguration<VolunteerEventProfile>
{
    public void Configure(EntityTypeBuilder<VolunteerEventProfile> builder)
    {
        builder.ToTable("volunteer_event_profiles");
        builder.HasKey(v => v.Id);

        builder.HasIndex(v => v.UserId).IsUnique();

        var listComparer = new ValueComparer<List<string>>(
            (a, b) => a != null && b != null && a.SequenceEqual(b),
            v => v.Aggregate(0, HashCode.Combine),
            v => v.ToList());

        ConfigureJsonbList(builder, v => v.Skills, listComparer);
        ConfigureJsonbList(builder, v => v.Quirks, listComparer);
        ConfigureJsonbList(builder, v => v.Languages, listComparer);

        // RETAINED dietary/medical columns — moved to Profile, kept for prod-soak
        // drop (memory/architecture/no-drops-until-prod-verified.md). Mapping stays
        // so EF keeps the columns; no code reads them.
        ConfigureJsonbList(builder, v => v.Allergies, listComparer);
        ConfigureJsonbList(builder, v => v.Intolerances, listComparer);
        builder.Property(v => v.AllergyOtherText).HasMaxLength(500);
        builder.Property(v => v.IntoleranceOtherText).HasMaxLength(500);
        builder.Property(v => v.DietaryPreference).HasMaxLength(200);
        builder.Property(v => v.MedicalConditions).HasMaxLength(4000);

        // UserId is a bare cross-section Guid column — no FK constraint, no nav.
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
