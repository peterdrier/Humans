using Humans.Issues.Domain;
using Humans.Issues.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Issues.Data;

/// <summary>
/// Per-section database context for the Issues section: maps only <c>issues</c> and
/// <c>issue_comments</c>, with its own <c>__EFMigrationsHistory_Issues</c>
/// table and migrations under <c>Migrations/Issues/</c>. Same database, same
/// connection — the split is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like every section context: repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// Reporters, assignees and commenters are bare Guid user references, so the
/// Identity tables stay in <see cref="UsersDbContext"/> and are deliberately
/// absent here.
/// </remarks>
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
