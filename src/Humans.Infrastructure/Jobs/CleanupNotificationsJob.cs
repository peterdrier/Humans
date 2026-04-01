using Humans.Application.Interfaces;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Purges resolved notifications older than 7 days. Runs daily.
/// CASCADE deletes NotificationRecipients automatically.
/// </summary>
public class CleanupNotificationsJob : IRecurringJob
{
    private static readonly Duration RetentionPeriod = Duration.FromDays(7);

    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly HumansMetricsService _metrics;
    private readonly ILogger<CleanupNotificationsJob> _logger;

    public CleanupNotificationsJob(
        HumansDbContext dbContext,
        IClock clock,
        HumansMetricsService metrics,
        ILogger<CleanupNotificationsJob> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = _clock.GetCurrentInstant() - RetentionPeriod;

        var toDelete = await _dbContext.Notifications
            .Where(n => n.ResolvedAt != null && n.ResolvedAt < cutoff)
            .ToListAsync(cancellationToken);

        if (toDelete.Count > 0)
        {
            _dbContext.Notifications.RemoveRange(toDelete);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation(
            "CleanupNotificationsJob deleted {Count} resolved notifications older than {Cutoff}",
            toDelete.Count, cutoff);

        _metrics.RecordJobRun("cleanup_notifications", "success");
    }
}
