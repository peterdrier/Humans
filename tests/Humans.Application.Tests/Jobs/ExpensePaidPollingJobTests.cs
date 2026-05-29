using Humans.Application.Interfaces.Expenses;
using Humans.Infrastructure.Jobs;
using NSubstitute;

namespace Humans.Application.Tests.Jobs;

/// <summary>
/// Smoke tests: the job is a thin wrapper that delegates to
/// <see cref="IExpenseReportBackgroundProcessor.PollHoldedPaidStatusAsync"/> with the right batch size.
/// Business logic is covered by <c>ExpenseReportServiceHoldedPollingTests</c>.
/// </summary>
public class ExpensePaidPollingJobTests
{
    [HumansFact]
    public async Task ExecuteAsync_DelegatesToService_WithBatchSize50()
    {
        var expenses = Substitute.For<IExpenseReportBackgroundProcessor>();
        var job = new ExpensePaidPollingJob(expenses);

        await job.ExecuteAsync();

        await expenses.Received(1).PollHoldedPaidStatusAsync(50, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_PassesCancellationTokenThrough()
    {
        var expenses = Substitute.For<IExpenseReportBackgroundProcessor>();
        var job = new ExpensePaidPollingJob(expenses);
        using var cts = new CancellationTokenSource();

        await job.ExecuteAsync(cts.Token);

        await expenses.Received(1).PollHoldedPaidStatusAsync(50, cts.Token);
    }
}
