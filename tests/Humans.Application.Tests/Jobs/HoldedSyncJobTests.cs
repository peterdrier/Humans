using Humans.Application.Interfaces.Finance;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services.Holded;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Application.Tests.Jobs;

/// <summary>
/// The nightly Holded sync must no-op when no API key is configured (PR-preview / local dev),
/// and run both the actuals + creditor-ledger syncs when a key is present.
/// </summary>
public class HoldedSyncJobTests
{
    private static HoldedSyncJob MakeJob(IHoldedFinanceService finance, string apiKey) =>
        new(finance, Options.Create(new HoldedClientOptions { ApiKey = apiKey }),
            NullLogger<HoldedSyncJob>.Instance);

    [HumansFact]
    public async Task ExecuteAsync_SkipsHolded_WhenNoApiKey()
    {
        var finance = Substitute.For<IHoldedFinanceService>();
        var job = MakeJob(finance, apiKey: "");

        await job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        await finance.DidNotReceive().SyncAsync(Arg.Any<CancellationToken>());
        await finance.DidNotReceive().SyncCreditorLedgerAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_RunsBothSyncs_WhenApiKeyPresent()
    {
        var finance = Substitute.For<IHoldedFinanceService>();
        var job = MakeJob(finance, apiKey: "k");

        await job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        await finance.Received(1).SyncAsync(Arg.Any<CancellationToken>());
        await finance.Received(1).SyncCreditorLedgerAsync(Arg.Any<CancellationToken>());
    }
}
