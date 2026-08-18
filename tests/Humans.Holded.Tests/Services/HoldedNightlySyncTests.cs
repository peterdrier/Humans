using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Holded.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Holded.Tests.Services;

/// <summary>
/// The nightly Holded sync must no-op when no API key is configured (PR-preview / local dev),
/// and run both the doc sync (Finance) and the ledger-mirror sync (Holded section) when a key
/// is present. The Hangfire target <c>HoldedSyncJob</c> is a shim over this — see
/// <see cref="IHoldedNightlySync"/>.
/// </summary>
public class HoldedNightlySyncTests
{
    private static HoldedNightlySync MakeSync(IHoldedFinanceService finance, IHoldedService holded, string apiKey) =>
        new(finance, holded, Options.Create(new HoldedClientOptions { ApiKey = apiKey }),
            NullLogger<HoldedNightlySync>.Instance);

    [HumansFact]
    public async Task RunAsync_SkipsHolded_WhenNoApiKey()
    {
        var finance = Substitute.For<IHoldedFinanceService>();
        var holded = Substitute.For<IHoldedService>();
        var sync = MakeSync(finance, holded, apiKey: "");

        await sync.RunAsync(Xunit.TestContext.Current.CancellationToken);

        await finance.DidNotReceive().SyncAsync(Arg.Any<CancellationToken>());
        await holded.DidNotReceive().SyncLedgerAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RunAsync_RunsBothSyncs_WhenApiKeyPresent()
    {
        var finance = Substitute.For<IHoldedFinanceService>();
        var holded = Substitute.For<IHoldedService>();
        var sync = MakeSync(finance, holded, apiKey: "k");

        await sync.RunAsync(Xunit.TestContext.Current.CancellationToken);

        await finance.Received(1).SyncAsync(Arg.Any<CancellationToken>());
        await holded.Received(1).SyncLedgerAsync(false, Arg.Any<CancellationToken>());
    }
}
