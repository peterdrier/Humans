using Hangfire;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Expenses;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Drains the Holded expense outbox: creates or updates purchase documents in Holded
/// for each approved expense report.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class HoldedExpenseOutboxJob : IRecurringJob
{
    private const int BatchSize = 100;

    private readonly IExpenseReportService _expenseService;

    public HoldedExpenseOutboxJob(IExpenseReportService expenseService)
    {
        _expenseService = expenseService;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken = default) =>
        _expenseService.DrainHoldedOutboxAsync(BatchSize, cancellationToken);
}
