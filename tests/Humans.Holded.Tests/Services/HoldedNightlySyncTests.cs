using AwesomeAssertions;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Holded.Services;
using Humans.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Holded.Tests.Services;

/// <summary>
/// The nightly Holded sync must no-op when no API key is configured (PR-preview / local dev),
/// and run both the doc sync (Finance) and the ledger-mirror sync (Holded section) when a key
/// is present. A sweep the gate refuses is reported, never silent. The Hangfire target
/// <c>HoldedSyncJob</c> is a shim over this — see <see cref="IHoldedNightlySync"/>.
/// </summary>
public class HoldedNightlySyncTests
{
    private static HoldedNightlySync MakeSync(
        IHoldedFinanceService finance,
        IHoldedService holded,
        string apiKey,
        ILogger<HoldedNightlySync>? logger = null) =>
        new(finance, holded, Options.Create(new HoldedClientOptions { ApiKey = apiKey }),
            logger ?? NullLogger<HoldedNightlySync>.Instance);

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

    [HumansFact]
    public async Task RunAsync_WarnsWhenSweepSkipped()
    {
        var finance = Substitute.For<IHoldedFinanceService>();
        var holded = Substitute.For<IHoldedService>();
        holded.SyncLedgerAsync(false, Arg.Any<CancellationToken>()).Returns(false);
        var logger = new CapturingLogger<HoldedNightlySync>();
        var sync = MakeSync(finance, holded, apiKey: "k", logger);

        await sync.RunAsync(Xunit.TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("already running"));
    }

    [HumansFact]
    public async Task RunAsync_IsQuietWhenSweepRan()
    {
        var finance = Substitute.For<IHoldedFinanceService>();
        var holded = Substitute.For<IHoldedService>();
        holded.SyncLedgerAsync(false, Arg.Any<CancellationToken>()).Returns(true);
        var logger = new CapturingLogger<HoldedNightlySync>();
        var sync = MakeSync(finance, holded, apiKey: "k", logger);

        await sync.RunAsync(Xunit.TestContext.Current.CancellationToken);

        logger.Entries.Should().BeEmpty();
    }
}
