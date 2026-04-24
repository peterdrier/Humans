using Hangfire;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Purges old notifications. Runs daily.
/// - Resolved notifications older than 7 days
/// - Unresolved informational notifications older than 30 days
/// Actionable notifications are never auto-cleaned (they represent real work items).
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class CleanupNotificationsJob : IRecurringJob
{
    private static readonly Duration ResolvedRetentionPeriod = Duration.FromDays(7);
    private static readonly Duration InformationalRetentionPeriod = Duration.FromDays(30);

    private readonly INotificationRepository _notificationRepository;
    private readonly IClock _clock;
    private readonly IHumansMetrics _metrics;
    private readonly ILogger<CleanupNotificationsJob> _logger;

    public CleanupNotificationsJob(
        INotificationRepository notificationRepository,
        IClock clock,
        IHumansMetrics metrics,
        ILogger<CleanupNotificationsJob> logger)
    {
        _notificationRepository = notificationRepository;
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

            var resolvedDeleted = await _notificationRepository
                .DeleteResolvedOlderThanAsync(resolvedCutoff, cancellationToken);

            var staleDeleted = await _notificationRepository
                .DeleteUnresolvedInformationalOlderThanAsync(informationalCutoff, cancellationToken);

            _logger.LogInformation(
                "CleanupNotificationsJob: deleted {ResolvedCount} resolved (>{ResolvedDays}d) and {StaleCount} stale informational (>{StaleDays}d) notifications",
                resolvedDeleted, ResolvedRetentionPeriod.Days,
                staleDeleted, InformationalRetentionPeriod.Days);

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
