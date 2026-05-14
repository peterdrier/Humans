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

        builder.HasIndex(f => new { f.UserId, f.GuideEventId }).IsUnique();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(f => f.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(f => f.Event)
            .WithMany(e => e.EventFavourites)
            .HasForeignKey(f => f.GuideEventId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
