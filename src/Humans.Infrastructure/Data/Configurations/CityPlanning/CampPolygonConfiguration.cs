using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Application.Architecture;

namespace Humans.Infrastructure.Data.Configurations.CityPlanning;

[Grandfathered(
    ruleId: "HUM0024",
    justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.",
    since: "2026-05-25",
    issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]
public class CampPolygonConfiguration : IEntityTypeConfiguration<CampPolygon>
{
    public void Configure(EntityTypeBuilder<CampPolygon> builder)
    {
        builder.ToTable("camp_polygons");

        // One polygon per camp season
        builder.HasIndex(p => p.CampSeasonId).IsUnique();

        builder.Property(p => p.GeoJson).HasColumnType("text").IsRequired();

        builder.HasOne(p => p.CampSeason)
            .WithMany()
            .HasForeignKey(p => p.CampSeasonId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.LastModifiedByUser)
            .WithMany()
            .HasForeignKey(p => p.LastModifiedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
