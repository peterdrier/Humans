using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Events.Data;

internal sealed class EventGuideDbContextFactory : SectionDesignTimeDbContextFactory<EventGuideDbContext>
{
    protected override EventGuideDbContext Create(DbContextOptions<EventGuideDbContext> options) => new(options);
}
