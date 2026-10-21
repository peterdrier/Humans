using AwesomeAssertions;
using Humans.Holded.Contracts;
using Humans.Holded.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Holded.Tests.Services;

/// <summary>
/// The nightly Holded sync must no-op when no API key is configured (PR-preview / local dev),
/// and run the ledger-mirror sweep when a key is present. Purchase docs are Finance's own job.
/// A sweep the gate refuses is reported, never silent. The Hangfire target
/// <c>HoldedSyncJob</c> is a shim over this — see <see cref="IHoldedNightlySync"/>.
/// </summary>
public class HoldedNightlySyncTests
{
    private static HoldedNightlySync MakeSync(
        IHoldedService holded,
        string apiKey,
        ILogger<HoldedNightlySync>? logger = null) =>
        new(holded, Options.Create(new HoldedClientOptions { ApiKey = apiKey }),
            logger ?? NullLogger<HoldedNightlySync>.Instance);

    [HumansFact]
    public async Task RunAsync_SkipsHolded_WhenNoApiKey()
    {
        var holded = Substitute.For<IHoldedService>();
        var logger = new CapturingLogger<HoldedNightlySync>();
        var sync = MakeSync(holded, apiKey: "", logger);

        await sync.RunAsync(Xunit.TestContext.Current.CancellationToken);

        await holded.DidNotReceive().SyncLedgerAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Information && e.Message.Contains("not configured"));
    }

    [HumansFact]
    public async Task RunAsync_RunsLedgerSweep_WhenApiKeyPresent()
    {
        var holded = Substitute.For<IHoldedService>();
        var sync = MakeSync(holded, apiKey: "k");

        await sync.RunAsync(Xunit.TestContext.Current.CancellationToken);

        await holded.Received(1).SyncLedgerAsync(false, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RunAsync_WarnsWhenSweepSkipped()
    {
        var holded = Substitute.For<IHoldedService>();
        holded.SyncLedgerAsync(false, Arg.Any<CancellationToken>()).Returns(false);
        var logger = new CapturingLogger<HoldedNightlySync>();
        var sync = MakeSync(holded, apiKey: "k", logger);

        await sync.RunAsync(Xunit.TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("already running"));
    }

    [HumansFact]
    public async Task RunAsync_IsQuietWhenSweepRan()
    {
        var holded = Substitute.For<IHoldedService>();
        holded.SyncLedgerAsync(false, Arg.Any<CancellationToken>()).Returns(true);
        var logger = new CapturingLogger<HoldedNightlySync>();
        var sync = MakeSync(holded, apiKey: "k", logger);

        await sync.RunAsync(Xunit.TestContext.Current.CancellationToken);

        logger.Entries.Should().BeEmpty();
    }
}
