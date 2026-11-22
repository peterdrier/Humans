using Humans.CityPlanning.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.CityPlanning.Data.Configurations;

internal sealed class CityPlanningSettingsConfiguration : IEntityTypeConfiguration<CityPlanningSettings>
{
    public void Configure(EntityTypeBuilder<CityPlanningSettings> builder)
    {
        builder.ToTable("city_settings");

        builder.HasIndex(s => s.Year).IsUnique();

        // Column keeps its old name: stored true/false read as forced open/closed.
        builder.Property(s => s.PlacementOverride).HasColumnName("IsPlacementOpen");

        builder.Property(s => s.RegistrationInfo).HasColumnType("text");
        builder.Property(s => s.LimitZoneGeoJson).HasColumnType("text");
        builder.Property(s => s.OfficialZonesGeoJson).HasColumnType("text");
    }
}
