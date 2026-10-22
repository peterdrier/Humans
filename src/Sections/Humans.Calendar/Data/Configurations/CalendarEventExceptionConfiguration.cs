using Humans.Calendar.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Calendar.Data.Configurations;

internal sealed class CalendarEventExceptionConfiguration : IEntityTypeConfiguration<CalendarEventException>
{
    public void Configure(EntityTypeBuilder<CalendarEventException> b)
    {
        b.ToTable("calendar_event_exceptions");
        b.HasKey(x => x.Id);

        b.Property(x => x.OverrideTitle).HasMaxLength(200);
        b.Property(x => x.OverrideDescription).HasMaxLength(4000);
        b.Property(x => x.OverrideLocation).HasMaxLength(500);
        b.Property(x => x.OverrideLocationUrl).HasMaxLength(2000);

        b.HasIndex(x => new { x.EventId, x.OriginalOccurrenceStartUtc })
         .IsUnique();

        // All-day exceptions leave OriginalOccurrenceStartUtc null, which the index above doesn't constrain.
        b.HasIndex(x => new { x.EventId, x.OriginalOccurrenceDate })
         .IsUnique();

        // Matches the parent's soft-delete filter, so exceptions of a deleted event aren't returned.
        b.HasQueryFilter(ex => ex.Event.DeletedAt == null);
    }
}
