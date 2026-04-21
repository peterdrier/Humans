using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class CalendarEventExceptionConfiguration : IEntityTypeConfiguration<CalendarEventException>
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
    }
}
