using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Camps;

public class CampSeasonShiftObligationConfiguration : IEntityTypeConfiguration<CampSeasonShiftObligation>
{
    public void Configure(EntityTypeBuilder<CampSeasonShiftObligation> builder)
    {
        builder.ToTable("camp_season_shift_obligations");

        builder.HasOne(o => o.ShiftObligation)
            .WithMany(o => o.Overrides)
            .HasForeignKey(o => o.ShiftObligationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<CampSeason>()
            .WithMany()
            .HasForeignKey(o => o.CampSeasonId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(o => new { o.CampSeasonId, o.ShiftObligationId })
            .IsUnique()
            .HasDatabaseName("IX_camp_season_shift_obligations_unique");

        builder.HasIndex(o => o.CampSeasonId);
    }
}
