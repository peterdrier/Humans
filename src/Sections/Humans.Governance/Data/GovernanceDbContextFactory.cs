using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Governance.Data;

internal sealed class GovernanceDbContextFactory : SectionDesignTimeDbContextFactory<GovernanceDbContext>
{
    protected override GovernanceDbContext Create(DbContextOptions<GovernanceDbContext> options) => new(options);
}
