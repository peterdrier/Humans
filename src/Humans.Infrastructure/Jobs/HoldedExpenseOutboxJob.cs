using Hangfire;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Expenses;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Drains the Holded expense outbox: creates or updates purchase documents in Holded
/// for each approved expense report.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class HoldedExpenseOutboxJob(IExpenseReportBackgroundProcessor expenses) : IRecurringJob
{
    private const int BatchSize = 100;

    public Task ExecuteAsync(CancellationToken cancellationToken = default) =>
        expenses.DrainHoldedOutboxAsync(BatchSize, cancellationToken);
}
