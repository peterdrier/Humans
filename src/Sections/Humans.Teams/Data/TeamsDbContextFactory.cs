using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Teams.Data;

internal sealed class TeamsDbContextFactory : SectionDesignTimeDbContextFactory<TeamsDbContext>
{
    protected override TeamsDbContext Create(DbContextOptions<TeamsDbContext> options) => new(options);
}
