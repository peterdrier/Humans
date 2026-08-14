using Hangfire;
using Humans.Application.Interfaces;
using Humans.Expenses.Contracts;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Drains the Holded expense outbox: creates or updates purchase documents in Holded
/// for each approved expense report. Scheduler shim only — the queue semantics (backoff,
/// retry ceiling, and the skip when no API key is configured) live in the section, next to
/// the state they read.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class HoldedExpenseOutboxJob(
    IExpenseReportBackgroundProcessor expenses) : IRecurringJob
{
    private const int BatchSize = 100;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await expenses.DrainHoldedOutboxAsync(BatchSize, cancellationToken);
    }
}
