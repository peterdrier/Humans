using Hangfire;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Expenses;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Polls Holded for payment status on SepaSent expense reports.
/// Runs every 15 minutes; caps at 50 reports per run (oldest SepaSentAt first).
/// Transitions reports whose Holded purchase document shows PaymentsPending == 0
/// and ApprovedAt != null to the Paid state.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 120)]
public class ExpensePaidPollingJob(IExpenseReportService expenseService) : IRecurringJob
{
    private const int BatchSize = 50;

    public Task ExecuteAsync(CancellationToken cancellationToken = default) =>
        expenseService.PollHoldedPaidStatusAsync(BatchSize, cancellationToken);
}
