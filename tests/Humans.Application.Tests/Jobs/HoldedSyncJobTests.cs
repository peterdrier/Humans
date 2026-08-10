using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services.Holded;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Application.Tests.Jobs;

/// <summary>
/// The nightly Holded sync must no-op when no API key is configured (PR-preview / local dev),
/// and run both the doc sync (Finance) and the ledger-mirror sync (Holded section) when a key
/// is present.
/// </summary>
public class HoldedSyncJobTests
{
    private static HoldedSyncJob MakeJob(IHoldedFinanceService finance, IHoldedService holded, string apiKey) =>
        new(finance, holded, Options.Create(new HoldedClientOptions { ApiKey = apiKey }),
            NullLogger<HoldedSyncJob>.Instance);

    [HumansFact]
    public async Task ExecuteAsync_SkipsHolded_WhenNoApiKey()
    {
        var finance = Substitute.For<IHoldedFinanceService>();
        var holded = Substitute.For<IHoldedService>();
        var job = MakeJob(finance, holded, apiKey: "");

        await job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        await finance.DidNotReceive().SyncAsync(Arg.Any<CancellationToken>());
        await holded.DidNotReceive().SyncLedgerAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_RunsBothSyncs_WhenApiKeyPresent()
    {
        var finance = Substitute.For<IHoldedFinanceService>();
        var holded = Substitute.For<IHoldedService>();
        var job = MakeJob(finance, holded, apiKey: "k");

        await job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        await finance.Received(1).SyncAsync(Arg.Any<CancellationToken>());
        await holded.Received(1).SyncLedgerAsync(false, Arg.Any<CancellationToken>());
    }
}
