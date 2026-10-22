using Humans.Calendar.Domain;
using Humans.Calendar.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Calendar.Data;

internal sealed class CalendarDbContext(DbContextOptions<CalendarDbContext> options)
    : DbContext(options)
{
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
    public DbSet<CalendarEventException> CalendarEventExceptions => Set<CalendarEventException>();
    public DbSet<CalendarFeedToken> CalendarFeedTokens => Set<CalendarFeedToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new CalendarEventConfiguration());
        builder.ApplyConfiguration(new CalendarEventExceptionConfiguration());
        builder.ApplyConfiguration(new CalendarFeedTokenConfiguration());
    }
}
