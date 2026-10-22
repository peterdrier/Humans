using Hangfire;
using Humans.Base.Interfaces;
using Humans.Expenses.Contracts;

namespace Humans.Expenses.Jobs;

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
