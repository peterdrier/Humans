using Humans.Application.Interfaces.Expenses;
using Humans.Infrastructure.Jobs;
using NSubstitute;

namespace Humans.Application.Tests.Jobs;

/// <summary>
/// Smoke tests: the job is a thin wrapper that delegates to
/// <see cref="IExpenseReportBackgroundProcessor.DrainHoldedOutboxAsync"/> with the right batch size.
/// Business logic is covered by <c>ExpenseReportServiceHoldedOutboxTests</c>.
/// </summary>
public class HoldedExpenseOutboxJobTests
{
    [HumansFact]
    public async Task ExecuteAsync_DelegatesToService_WithBatchSize100()
    {
        var expenses = Substitute.For<IExpenseReportBackgroundProcessor>();
        var job = new HoldedExpenseOutboxJob(expenses);

        await job.ExecuteAsync();

        await expenses.Received(1).DrainHoldedOutboxAsync(100, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_PassesCancellationTokenThrough()
    {
        var expenses = Substitute.For<IExpenseReportBackgroundProcessor>();
        var job = new HoldedExpenseOutboxJob(expenses);
        using var cts = new CancellationTokenSource();

        await job.ExecuteAsync(cts.Token);

        await expenses.Received(1).DrainHoldedOutboxAsync(100, cts.Token);
    }
}
