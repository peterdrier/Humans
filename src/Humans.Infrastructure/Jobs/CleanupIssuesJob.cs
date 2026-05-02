using Hangfire;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Issues;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Purges issues that entered a terminal state (Resolved / WontFix / Duplicate)
/// at least 6 months ago, plus their screenshot directories. Runs daily.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class CleanupIssuesJob : IRecurringJob
{
    private readonly IIssuesService _issues;
    private readonly IHumansMetrics _metrics;
    private readonly ILogger<CleanupIssuesJob> _logger;

    public CleanupIssuesJob(
        IIssuesService issues,
        IHumansMetrics metrics,
        ILogger<CleanupIssuesJob> logger)
    {
        _issues = issues;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var deleted = await _issues.PurgeExpiredAsync(cancellationToken);

            _logger.LogInformation(
                "CleanupIssuesJob: deleted {Count} expired issues",
                deleted);

            _metrics.RecordJobRun("cleanup_issues", "success");
        }
        catch (Exception ex)
        {
            _metrics.RecordJobRun("cleanup_issues", "failure");
            _logger.LogError(ex, "Error cleaning up expired issues");
            throw;
        }
    }
}
