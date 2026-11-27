using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Agent.Data;

internal sealed class AgentDbContextFactory : SectionDesignTimeDbContextFactory<AgentDbContext>
{
    protected override AgentDbContext Create(DbContextOptions<AgentDbContext> options) => new(options);
}
