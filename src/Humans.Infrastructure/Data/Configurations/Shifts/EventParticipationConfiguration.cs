using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Shifts;

public class EventParticipationConfiguration : IEntityTypeConfiguration<EventParticipation>
{
    public void Configure(EntityTypeBuilder<EventParticipation> builder)
    {
        builder.ToTable("event_participations");

        builder.HasKey(ep => ep.Id);

        builder.Property(ep => ep.UserId)
            .IsRequired();

        builder.Property(ep => ep.Year)
            .IsRequired();

        builder.Property(ep => ep.Status)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(ep => ep.Source)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        // Unique constraint on (UserId, Year)
        builder.HasIndex(ep => new { ep.UserId, ep.Year })
            .IsUnique();

        // EF needs the nav ref to configure the cross-section FK + cascade.
        // The nav is [Obsolete] for the Application layer (design-rules §6c).
#pragma warning disable CS0618
        builder.HasOne(ep => ep.User)
            .WithMany(u => u.EventParticipations)
            .HasForeignKey(ep => ep.UserId)
            .OnDelete(DeleteBehavior.Cascade);
#pragma warning restore CS0618
    }
}
