using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Issues.Data;

internal sealed class IssuesDbContextFactory : SectionDesignTimeDbContextFactory<IssuesDbContext>
{
    protected override IssuesDbContext Create(DbContextOptions<IssuesDbContext> options) => new(options);
}
