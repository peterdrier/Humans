using Humans.Issues.Domain;
using Humans.Issues.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Issues.Data;

internal sealed class IssuesDbContext(DbContextOptions<IssuesDbContext> options)
    : DbContext(options)
{
    public DbSet<Issue> Issues => Set<Issue>();
    public DbSet<IssueComment> IssueComments => Set<IssueComment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new IssueConfiguration());
        builder.ApplyConfiguration(new IssueCommentConfiguration());
    }
}
