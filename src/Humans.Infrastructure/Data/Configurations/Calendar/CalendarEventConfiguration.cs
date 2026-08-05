using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Application.Architecture;

namespace Humans.Infrastructure.Data.Configurations.Calendar;

[Grandfathered(
    ruleId: "HUM0024",
    justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.",
    since: "2026-05-25",
    issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]
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

        // Cross-section FK column only — no nav property (design-rules §6c,
        // memory/architecture/no-cross-section-ef-joins.md).
        b.HasOne<Team>()
         .WithMany()
         .HasForeignKey(e => e.OwningTeamId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasMany(e => e.Exceptions)
         .WithOne(x => x.Event)
         .HasForeignKey(x => x.EventId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasQueryFilter(e => e.DeletedAt == null);

        b.HasIndex(e => new { e.OwningTeamId, e.StartUtc });
        b.HasIndex(e => new { e.StartUtc, e.RecurrenceUntilUtc });
    }
}
