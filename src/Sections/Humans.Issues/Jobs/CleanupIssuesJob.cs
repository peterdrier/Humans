using Hangfire;
using Humans.Base.Interfaces;
using Humans.Issues.Contracts;

namespace Humans.Issues.Jobs;

/// <summary>
/// Purges issues that entered a terminal state (Resolved / WontFix / Duplicate)
/// at least 6 months ago, plus their screenshot directories. Runs daily.
/// </summary>
/// <remarks>
/// Public and under <c>Jobs/</c> because Shell names the concrete type at registration and
/// HUM0034 makes every other public type in a section assembly an error. Hangfire does not
/// pin the job to an assembly: <c>RecurringJob.AddOrUpdate&lt;T&gt;(id, …)</c> rewrites the
/// stored type string on every startup, so the job id is the stable key.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class CleanupIssuesJob(IIssuesRetention issues, IHumansMetrics metrics, ILogger<CleanupIssuesJob> logger)
    : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var deleted = await issues.PurgeExpiredAsync(cancellationToken);

            logger.LogInformation(
                "CleanupIssuesJob: deleted {Count} expired issues",
                deleted);

            metrics.RecordJobRun("cleanup_issues", "success");
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("cleanup_issues", "failure");
            logger.LogError(ex, "Error cleaning up expired issues");
            throw;
        }
    }
}
