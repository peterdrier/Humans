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
public class CampPolygonHistoryConfiguration : IEntityTypeConfiguration<CampPolygonHistory>
{
    public void Configure(EntityTypeBuilder<CampPolygonHistory> builder)
    {
        builder.ToTable("camp_polygon_histories");

        builder.HasIndex(h => new { h.CampSeasonId, h.ModifiedAt });

        builder.Property(h => h.GeoJson).HasColumnType("text").IsRequired();
        builder.Property(h => h.Note).HasMaxLength(512).IsRequired();

        builder.HasOne(h => h.CampSeason)
            .WithMany()
            .HasForeignKey(h => h.CampSeasonId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(h => h.ModifiedByUser)
            .WithMany()
            .HasForeignKey(h => h.ModifiedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
