using System.Text.Json;
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Camps;

public class CampSettingsConfiguration : IEntityTypeConfiguration<CampSettings>
{
    public void Configure(EntityTypeBuilder<CampSettings> builder)
    {
        builder.ToTable("camp_settings");

        builder.Property(s => s.OpenSeasons).HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<int>>(v, (JsonSerializerOptions?)null) ?? new(),
                new ValueComparer<List<int>>(
                    (a, b) => a != null && b != null && a.SequenceEqual(b),
                    v => v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
                    v => v.ToList()));

        // Reserved GUID block: 0010. See docs/guid-reservations.md.
        builder.HasData(new CampSettings
        {
            Id = Guid.Parse("00000000-0000-0000-0010-000000000001"),
            PublicYear = 2026,
            OpenSeasons = new List<int> { 2026 }
        });
    }
}
