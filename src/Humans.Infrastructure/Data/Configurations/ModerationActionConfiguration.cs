using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class ModerationActionConfiguration : IEntityTypeConfiguration<ModerationAction>
{
    public void Configure(EntityTypeBuilder<ModerationAction> builder)
    {
        builder.ToTable("moderation_actions");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Action)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(m => m.Reason).HasMaxLength(500);

        builder.HasIndex(m => m.GuideEventId);

        builder.HasOne(m => m.GuideEvent)
            .WithMany(e => e.ModerationActions)
            .HasForeignKey(m => m.GuideEventId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(m => m.ActorUser)
            .WithMany()
            .HasForeignKey(m => m.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
