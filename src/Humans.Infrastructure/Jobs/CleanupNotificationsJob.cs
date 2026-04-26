using System.Diagnostics.Metrics;
using Humans.Application.Interfaces.Metering;
using Humans.Application.Metering;
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
public class CleanupNotificationsJob : IRecurringJob
{
    private static readonly Duration ResolvedRetentionPeriod = Duration.FromDays(7);
    private static readonly Duration InformationalRetentionPeriod = Duration.FromDays(30);

    private readonly INotificationRepository _notificationRepository;
    private readonly IClock _clock;
    private readonly Counter<long> _jobRunsCounter;
    private readonly ILogger<CleanupNotificationsJob> _logger;

    public CleanupNotificationsJob(
        INotificationRepository notificationRepository,
        IClock clock,
        IMeters meters,
        ILogger<CleanupNotificationsJob> logger)
    {
        _notificationRepository = notificationRepository;
        _clock = clock;
        _jobRunsCounter = meters.RegisterCounter(
            "humans.job_runs_total",
            new MeterMetadata("Total background job runs", "{runs}"));
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

            _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "cleanup_notifications"),
                new KeyValuePair<string, object?>("result", "success"));
        }
        catch (Exception ex)
        {
            _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "cleanup_notifications"),
                new KeyValuePair<string, object?>("result", "failure"));
            _logger.LogError(ex, "Error cleaning up notifications");
            throw;
        }
    }
}
