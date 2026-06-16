using Humans.Application.Interfaces.Expenses;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services.Holded;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Application.Tests.Jobs;

/// <summary>
/// Smoke tests: the job is a thin wrapper that delegates to
/// <see cref="IExpenseReportBackgroundProcessor.DrainHoldedOutboxAsync"/> with the right batch size,
/// and no-ops when no Holded API key is configured.
/// Business logic is covered by <c>ExpenseReportServiceHoldedOutboxTests</c>.
/// </summary>
public class HoldedExpenseOutboxJobTests
{
    private static HoldedExpenseOutboxJob MakeJob(IExpenseReportBackgroundProcessor expenses, string apiKey = "test-key") =>
        new(expenses, Options.Create(new HoldedClientOptions { ApiKey = apiKey }),
            NullLogger<HoldedExpenseOutboxJob>.Instance);

    [HumansFact]
    public async Task ExecuteAsync_DelegatesToService_WithBatchSize100()
    {
        var expenses = Substitute.For<IExpenseReportBackgroundProcessor>();
        var job = MakeJob(expenses);

        await job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        await expenses.Received(1).DrainHoldedOutboxAsync(100, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_PassesCancellationTokenThrough()
    {
        var expenses = Substitute.For<IExpenseReportBackgroundProcessor>();
        var job = MakeJob(expenses);
        using var cts = new CancellationTokenSource();

        await job.ExecuteAsync(cts.Token);

        await expenses.Received(1).DrainHoldedOutboxAsync(100, cts.Token);
    }

    [HumansFact]
    public async Task ExecuteAsync_SkipsDrain_WhenNoApiKey()
    {
        var expenses = Substitute.For<IExpenseReportBackgroundProcessor>();
        var job = MakeJob(expenses, apiKey: "");

        await job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        await expenses.DidNotReceive().DrainHoldedOutboxAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
