using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

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
