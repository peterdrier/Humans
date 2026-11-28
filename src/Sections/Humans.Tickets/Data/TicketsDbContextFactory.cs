using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Tickets.Data;

internal sealed class TicketsDbContextFactory : SectionDesignTimeDbContextFactory<TicketsDbContext>
{
    protected override TicketsDbContext Create(DbContextOptions<TicketsDbContext> options) => new(options);
}
