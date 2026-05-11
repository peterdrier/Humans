using Humans.Application.Interfaces.Expenses;
using Humans.Infrastructure.Jobs;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Jobs;

/// <summary>
/// Smoke tests: the job is a thin wrapper that delegates to
/// <see cref="IExpenseReportService.DrainHoldedOutboxAsync"/> with the right batch size.
/// Business logic is covered by <c>ExpenseReportServiceHoldedOutboxTests</c>.
/// </summary>
public class HoldedExpenseOutboxJobTests
{
    [HumansFact]
    public async Task ExecuteAsync_DelegatesToService_WithBatchSize100()
    {
        var service = Substitute.For<IExpenseReportService>();
        var job = new HoldedExpenseOutboxJob(service);

        await job.ExecuteAsync();

        await service.Received(1).DrainHoldedOutboxAsync(100, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_PassesCancellationTokenThrough()
    {
        var service = Substitute.For<IExpenseReportService>();
        var job = new HoldedExpenseOutboxJob(service);
        using var cts = new CancellationTokenSource();

        await job.ExecuteAsync(cts.Token);

        await service.Received(1).DrainHoldedOutboxAsync(100, cts.Token);
    }
}
