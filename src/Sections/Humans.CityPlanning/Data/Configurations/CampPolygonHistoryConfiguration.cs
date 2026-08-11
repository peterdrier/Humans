using Humans.CityPlanning.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.CityPlanning.Data.Configurations;

internal sealed class CampPolygonHistoryConfiguration : IEntityTypeConfiguration<CampPolygonHistory>
{
    public void Configure(EntityTypeBuilder<CampPolygonHistory> builder)
    {
        builder.ToTable("camp_polygon_histories");

        builder.HasIndex(h => new { h.CampSeasonId, h.ModifiedAt });

        builder.Property(h => h.GeoJson).HasColumnType("text").IsRequired();
        builder.Property(h => h.Note).HasMaxLength(512).IsRequired();
    }
}
