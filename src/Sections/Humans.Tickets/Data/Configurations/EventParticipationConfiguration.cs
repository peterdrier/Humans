using Humans.Tickets.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Tickets.Data;

internal sealed class EventParticipationConfiguration : IEntityTypeConfiguration<EventParticipation>
{
    public void Configure(EntityTypeBuilder<EventParticipation> builder)
    {
        builder.ToTable("tickets_event_participations");

        builder.HasKey(ep => ep.Id);

        builder.Property(ep => ep.UserId)
            .IsRequired();

        builder.Property(ep => ep.Year)
            .IsRequired();

        // Nullable: a cleared row (see EventParticipation.Status).
        builder.Property(ep => ep.Status)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(ep => ep.Source)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.HasIndex(ep => new { ep.UserId, ep.Year })
            .IsUnique();
    }
}
