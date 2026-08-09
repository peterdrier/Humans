using Humans.Domain.Entities;
using Humans.Infrastructure.Data.Configurations.Feedback;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Per-section database context for the Feedback section
/// (nobodies-collective/Humans#858): maps only <c>feedback_reports</c> and
/// <c>feedback_messages</c>, with its own
/// <c>__EFMigrationsHistory_Feedback</c> table and migrations under
/// <c>Migrations/Feedback/</c>. Same database, same connection — the split is a
/// code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like <see cref="HumansDbContext"/> (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// Reporters, assignees and the routed team are bare Guid references, so the
/// Identity and Teams tables stay in <see cref="HumansDbContext"/> and are
/// deliberately absent here.
/// </remarks>
internal sealed class FeedbackDbContext(DbContextOptions<FeedbackDbContext> options)
    : DbContext(options)
{
    public DbSet<FeedbackReport> FeedbackReports => Set<FeedbackReport>();
    public DbSet<FeedbackMessage> FeedbackMessages => Set<FeedbackMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new FeedbackReportConfiguration());
        builder.ApplyConfiguration(new FeedbackMessageConfiguration());
    }
}
