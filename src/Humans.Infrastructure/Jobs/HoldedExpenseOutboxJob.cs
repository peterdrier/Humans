using Hangfire;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Expenses;
using Humans.Infrastructure.Services.Holded;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Drains the Holded expense outbox: creates or updates purchase documents in Holded
/// for each approved expense report.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class HoldedExpenseOutboxJob(
    IExpenseReportBackgroundProcessor expenses,
    IOptions<HoldedClientOptions> holdedOptions,
    ILogger<HoldedExpenseOutboxJob> logger) : IRecurringJob
{
    private const int BatchSize = 100;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // No Holded API key (e.g. PR-preview / local dev envs) → don't drain. A 401 here is a
        // permanent error that would mark each outbox event FailedPermanently. Debug-level since
        // this job runs every minute. Skip until a key is configured.
        if (string.IsNullOrWhiteSpace(holdedOptions.Value.ApiKey))
        {
            logger.LogDebug("HOLDED_API_KEY not configured — skipping Holded expense outbox drain.");
            return;
        }

        await expenses.DrainHoldedOutboxAsync(BatchSize, cancellationToken);
    }
}
