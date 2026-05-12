using Humans.Application.Interfaces.Expenses;
using Humans.Infrastructure.Jobs;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Jobs;

/// <summary>
/// Smoke tests: the job is a thin wrapper that delegates to
/// <see cref="IExpenseReportService.PollHoldedPaidStatusAsync"/> with the right batch size.
/// Business logic is covered by <c>ExpenseReportServiceHoldedPollingTests</c>.
/// </summary>
public class ExpensePaidPollingJobTests
{
    [HumansFact]
    public async Task ExecuteAsync_DelegatesToService_WithBatchSize50()
    {
        var service = Substitute.For<IExpenseReportService>();
        var job = new ExpensePaidPollingJob(service);

        await job.ExecuteAsync();

        await service.Received(1).PollHoldedPaidStatusAsync(50, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_PassesCancellationTokenThrough()
    {
        var service = Substitute.For<IExpenseReportService>();
        var job = new ExpensePaidPollingJob(service);
        using var cts = new CancellationTokenSource();

        await job.ExecuteAsync(cts.Token);

        await service.Received(1).PollHoldedPaidStatusAsync(50, cts.Token);
    }
}
