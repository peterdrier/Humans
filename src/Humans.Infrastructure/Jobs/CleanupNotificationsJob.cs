using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Purges old notifications. Runs daily.
/// - Resolved notifications older than 7 days
/// - Unresolved informational notifications older than 30 days
/// Actionable notifications are never auto-cleaned (they represent real work items).
/// </summary>
public class CleanupNotificationsJob : IRecurringJob
{
    private static readonly Duration ResolvedRetentionPeriod = Duration.FromDays(7);
    private static readonly Duration InformationalRetentionPeriod = Duration.FromDays(30);

    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly IHumansMetrics _metrics;
    private readonly ILogger<CleanupNotificationsJob> _logger;

    public CleanupNotificationsJob(
        HumansDbContext dbContext,
        IClock clock,
        IHumansMetrics metrics,
        ILogger<CleanupNotificationsJob> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var now = _clock.GetCurrentInstant();
            var resolvedCutoff = now - ResolvedRetentionPeriod;
            var informationalCutoff = now - InformationalRetentionPeriod;

            // Pass 1: resolved notifications older than 7 days
            var resolvedToDelete = await _dbContext.Notifications
                .Where(n => n.ResolvedAt != null && n.ResolvedAt < resolvedCutoff)
                .ToListAsync(cancellationToken);

            if (resolvedToDelete.Count > 0)
            {
                _dbContext.Notifications.RemoveRange(resolvedToDelete);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            // Pass 2: unresolved informational notifications older than 30 days
            var staleToDelete = await _dbContext.Notifications
                .Where(n => n.ResolvedAt == null &&
                            n.Class == NotificationClass.Informational &&
                            n.CreatedAt < informationalCutoff)
                .ToListAsync(cancellationToken);

            if (staleToDelete.Count > 0)
            {
                _dbContext.Notifications.RemoveRange(staleToDelete);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            _logger.LogInformation(
                "CleanupNotificationsJob: deleted {ResolvedCount} resolved (>{ResolvedDays}d) and {StaleCount} stale informational (>{StaleDays}d) notifications",
                resolvedToDelete.Count, ResolvedRetentionPeriod.Days,
                staleToDelete.Count, InformationalRetentionPeriod.Days);

            _metrics.RecordJobRun("cleanup_notifications", "success");
        }
        catch (Exception ex)
        {
            _metrics.RecordJobRun("cleanup_notifications", "failure");
            _logger.LogError(ex, "Error cleaning up notifications");
            throw;
        }
    }
}
