using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Settings.Contracts;
using Humans.Settings.Services;
using NodaTime;

namespace Humans.Settings.Jobs;

/// <summary>
/// Daily: asks every <see cref="IRetentionPeriodContributor"/> to delete what is older than the retention period, and
/// audits each deletion with its count. One contributor failing does not stop the others.
/// </summary>
internal sealed class RetentionPeriodJob(
    ISettingsService settings,
    IEnumerable<IRetentionPeriodContributor> contributors,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<RetentionPeriodJob> logger) : IRecurringJob
{
    public const string JobName = nameof(RetentionPeriodJob);

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var days = RetentionPeriod.Parse(await settings.GetValueAsync(RetentionPeriod.Key, cancellationToken));
        var cutoff = clock.GetCurrentInstant() - Duration.FromDays(days);
        var failed = false;

        foreach (var contributor in contributors)
        {
            int deleted;
            try
            {
                deleted = await contributor.DeleteOlderThanAsync(cutoff, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Retention period: deleting {Label} failed", contributor.Label);
                failed = true;
                continue;
            }

            if (deleted == 0)
                continue;

            await auditLog.LogAsync(
                SettingsAuditActions.RetentionPeriodDeleted, AuditEntityTypes.RetentionPeriod, Guid.Empty,
                $"Deleted {deleted} {contributor.Label} older than {days} days.", JobName);
        }

        // Rethrown after the loop so Hangfire shows the run as failed without skipping the other contributors.
        if (failed)
            throw new InvalidOperationException("At least one retention period deletion failed; see the log.");
    }
}
