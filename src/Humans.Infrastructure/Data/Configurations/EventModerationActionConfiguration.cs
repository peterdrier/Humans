using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class EventModerationActionConfiguration : IEntityTypeConfiguration<EventModerationAction>
{
    public void Configure(EntityTypeBuilder<EventModerationAction> builder)
    {
        builder.ToTable("event_moderation_actions");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Action)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(m => m.Reason).HasMaxLength(500);

        builder.HasIndex(m => m.GuideEventId);

        builder.HasOne(m => m.Event)
            .WithMany(e => e.EventModerationActions)
            .HasForeignKey(m => m.GuideEventId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(m => m.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
