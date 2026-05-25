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
public class CleanupNotificationsJob(
    INotificationRepository notificationRepository,
    IClock clock,
    IHumansMetrics metrics,
    ILogger<CleanupNotificationsJob> logger) : IRecurringJob
{
    private static readonly Duration ResolvedRetentionPeriod = Duration.FromDays(7);
    private static readonly Duration InformationalRetentionPeriod = Duration.FromDays(30);

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var now = clock.GetCurrentInstant();
            var resolvedCutoff = now - ResolvedRetentionPeriod;
            var informationalCutoff = now - InformationalRetentionPeriod;

            var resolvedDeleted = await notificationRepository
                .DeleteResolvedOlderThanAsync(resolvedCutoff, cancellationToken);

            var staleDeleted = await notificationRepository
                .DeleteUnresolvedInformationalOlderThanAsync(informationalCutoff, cancellationToken);

            logger.LogInformation(
                "CleanupNotificationsJob: deleted {ResolvedCount} resolved (>{ResolvedDays}d) and {StaleCount} stale informational (>{StaleDays}d) notifications",
                resolvedDeleted, ResolvedRetentionPeriod.Days,
                staleDeleted, InformationalRetentionPeriod.Days);

            metrics.RecordJobRun("cleanup_notifications", "success");
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("cleanup_notifications", "failure");
            logger.LogError(ex, "Error cleaning up notifications");
            throw;
        }
    }
}
