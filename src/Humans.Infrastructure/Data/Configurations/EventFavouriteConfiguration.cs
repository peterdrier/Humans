using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class EventFavouriteConfiguration : IEntityTypeConfiguration<EventFavourite>
{
    public void Configure(EntityTypeBuilder<EventFavourite> builder)
    {
        builder.ToTable("event_favourites");
        builder.HasKey(f => f.Id);

        // NULLS NOT DISTINCT (PG15+) so a user can't hold two whole-event
        // (null-day) favourites for the same event via a double-submit race.
        builder.HasIndex(f => new { f.UserId, f.GuideEventId, f.DayOffset }).IsUnique().AreNullsDistinct(false);

        builder.HasOne(f => f.Event)
            .WithMany(e => e.EventFavourites)
            .HasForeignKey(f => f.GuideEventId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
