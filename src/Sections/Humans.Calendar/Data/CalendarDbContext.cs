using Humans.Calendar.Domain;
using Humans.Calendar.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Calendar.Data;

/// <summary>
/// Per-section database context for the Calendar section
/// (nobodies-collective/Humans#858): maps only <c>calendar_events</c> and
/// <c>calendar_event_exceptions</c>, with its own
/// <c>__EFMigrationsHistory_Calendar</c> table and migrations under
/// <c>Migrations/Calendar/</c>. Same database, same connection — the split is a
/// code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like <see cref="HumansDbContext"/> (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// The owning team is a bare Guid, so the Teams tables stay in
/// <see cref="HumansDbContext"/> and are deliberately absent here.
/// </remarks>
internal sealed class CalendarDbContext(DbContextOptions<CalendarDbContext> options)
    : DbContext(options)
{
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
    public DbSet<CalendarEventException> CalendarEventExceptions => Set<CalendarEventException>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new CalendarEventConfiguration());
        builder.ApplyConfiguration(new CalendarEventExceptionConfiguration());
    }
}
