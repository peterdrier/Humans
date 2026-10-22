using Humans.Feedback.Domain;
using Humans.Feedback.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Feedback.Data;

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
