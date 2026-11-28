using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Calendar.Data;

internal sealed class CalendarDbContextFactory : SectionDesignTimeDbContextFactory<CalendarDbContext>
{
    protected override CalendarDbContext Create(DbContextOptions<CalendarDbContext> options) => new(options);
}
