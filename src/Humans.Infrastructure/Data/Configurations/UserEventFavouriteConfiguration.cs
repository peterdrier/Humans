using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class UserEventFavouriteConfiguration : IEntityTypeConfiguration<UserEventFavourite>
{
    public void Configure(EntityTypeBuilder<UserEventFavourite> builder)
    {
        builder.ToTable("user_event_favourites");
        builder.HasKey(f => f.Id);

        builder.HasIndex(f => new { f.UserId, f.GuideEventId }).IsUnique();

        builder.HasOne(f => f.User)
            .WithMany()
            .HasForeignKey(f => f.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(f => f.GuideEvent)
            .WithMany(e => e.UserEventFavourites)
            .HasForeignKey(f => f.GuideEventId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
