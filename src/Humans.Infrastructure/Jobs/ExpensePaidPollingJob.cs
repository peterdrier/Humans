using Hangfire;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Expenses;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Reconciles payment status on SepaSent expense reports against the Holded creditor balance.
/// Caps at 50 reports per run (oldest SepaSentAt first). Transitions a report to Paid when the
/// member's creditor account balance is settled (≥ 0) — treasury pays the account in aggregate.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 120)]
public class ExpensePaidPollingJob(IExpenseReportBackgroundProcessor expenses) : IRecurringJob
{
    private const int BatchSize = 50;

    public Task ExecuteAsync(CancellationToken cancellationToken = default) =>
        expenses.PollHoldedPaidStatusAsync(BatchSize, cancellationToken);
}
