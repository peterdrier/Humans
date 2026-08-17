using Hangfire;
using Humans.Application.Interfaces;
using Humans.Expenses.Contracts;

namespace Humans.Expenses.Jobs;

/// <summary>
/// Drains the Holded expense outbox: creates or updates purchase documents in Holded
/// for each approved expense report. Scheduler shim only — the queue semantics (backoff,
/// retry ceiling, and the skip when no API key is configured) live in the section, next to
/// the state they read.
/// </summary>
/// <remarks>
/// Moved out of <c>Humans.Infrastructure/Jobs</c> at G5 lane 5b-5
/// (nobodies-collective/Humans#866), so shim and body are both Expenses'. It sits under
/// <c>Jobs/</c> because Shell names the concrete type at registration and HUM0034
/// makes every other public type in a section an error.
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
