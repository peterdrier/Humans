using Hangfire;
using Humans.Application.Interfaces;
using Humans.Issues.Contracts;
using Microsoft.Extensions.Logging;

namespace Humans.Issues.Jobs;

/// <summary>
/// Purges issues that entered a terminal state (Resolved / WontFix / Duplicate)
/// at least 6 months ago, plus their screenshot directories. Runs daily.
/// </summary>
/// <remarks>
/// Moved out of <c>Humans.Infrastructure/Jobs</c> at G5 lane 5b-1
/// (nobodies-collective/Humans#866). The "Hangfire pins a job to its assembly" claim that
/// kept it in Base was re-measured and is false: <c>RecurringJob.AddOrUpdate&lt;T&gt;(id, …)</c>
/// rewrites the stored type string on every startup, so the job id is the stable key. It
/// sits under <c>Jobs/</c> because Shell names the concrete type at registration and
/// HUM0034 makes every other public type in a section assembly an error.
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
