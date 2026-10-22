using Humans.Events.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Events.Data.Configurations;

internal sealed class EventFavouriteConfiguration : IEntityTypeConfiguration<EventFavourite>
{
    public void Configure(EntityTypeBuilder<EventFavourite> builder)
    {
        builder.ToTable("event_favourites");
        builder.HasKey(f => f.Id);

        // NULLS NOT DISTINCT (PG15+) so a double-submit can't store two whole-event (null-day) favourites.
        builder.HasIndex(f => new { f.UserId, f.GuideEventId, f.DayOffset }).IsUnique().AreNullsDistinct(false);

        builder.HasOne(f => f.Event)
            .WithMany(e => e.EventFavourites)
            .HasForeignKey(f => f.GuideEventId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
