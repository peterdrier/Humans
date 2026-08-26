using Hangfire;
using Humans.Base.Interfaces;
using Humans.Expenses.Contracts;

namespace Humans.Expenses.Jobs;

/// <summary>
/// Drains the Holded expense outbox: creates or updates purchase documents in Holded
/// for each approved expense report. Scheduler shim only — the queue semantics (backoff,
/// retry ceiling, and the skip when no API key is configured) live in the section, next to
/// the state they read.
/// </summary>
/// <remarks>
/// It sits under <c>Jobs/</c> because Shell names the concrete type at registration and
/// HUM0034 makes every other public type in a section an error.
/// </remarks>
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
