using System.Text.Json;
using Humans.Cantina.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Cantina.Data.Configurations;

internal sealed class DietaryProfileConfiguration : IEntityTypeConfiguration<DietaryProfile>
{
    public void Configure(EntityTypeBuilder<DietaryProfile> b)
    {
        b.ToTable("cantina_dietary_profiles");
        b.HasKey(x => x.UserId);
        b.Property(x => x.UserId).ValueGeneratedNever();

        // Same column shapes as the profiles columns this replaces, so the copy is lossless.
        b.Property(x => x.DietaryPreference).HasMaxLength(200);
        b.Property(x => x.AllergyOtherText).HasMaxLength(500);
        b.Property(x => x.IntoleranceOtherText).HasMaxLength(500);
        b.Property(x => x.UpdatedAt).IsRequired();

        var listComparer = new ValueComparer<List<string>>(
            (a, c) => a != null && c != null && a.SequenceEqual(c),
            v => v.Aggregate(0, HashCode.Combine),
            v => v.ToList());
        foreach (var list in new[] { nameof(DietaryProfile.Allergies), nameof(DietaryProfile.Intolerances) })
        {
            b.Property<List<string>>(list).HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new(),
                    listComparer)
                .IsRequired();
        }
    }
}
