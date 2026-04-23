using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Calendar;

public class CalendarEventConfiguration : IEntityTypeConfiguration<CalendarEvent>
{
    public void Configure(EntityTypeBuilder<CalendarEvent> b)
    {
        b.ToTable("calendar_events");
        b.HasKey(e => e.Id);

        b.Property(e => e.Title).IsRequired().HasMaxLength(200);
        b.Property(e => e.Description).HasMaxLength(4000);
        b.Property(e => e.Location).HasMaxLength(500);
        b.Property(e => e.LocationUrl).HasMaxLength(2000);
        b.Property(e => e.RecurrenceRule).HasMaxLength(500);
        b.Property(e => e.RecurrenceTimezone).HasMaxLength(100);
        b.Property(e => e.OwningTeamId).IsRequired();

        // EF needs the nav ref to configure the cross-section FK + cascade
        // behavior. The nav itself is [Obsolete] for the Application layer
        // (design-rules §6c) — suppress the obsolete warning only for this
        // wiring block.
#pragma warning disable CS0618
        b.HasOne(e => e.OwningTeam)
         .WithMany()
         .HasForeignKey(e => e.OwningTeamId)
         .OnDelete(DeleteBehavior.Restrict);
#pragma warning restore CS0618

        b.HasMany(e => e.Exceptions)
         .WithOne(x => x.Event)
         .HasForeignKey(x => x.EventId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasQueryFilter(e => e.DeletedAt == null);

        b.HasIndex(e => new { e.OwningTeamId, e.StartUtc });
        b.HasIndex(e => new { e.StartUtc, e.RecurrenceUntilUtc });
    }
}
