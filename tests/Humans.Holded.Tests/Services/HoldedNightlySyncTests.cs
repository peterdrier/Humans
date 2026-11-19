using AwesomeAssertions;
using Humans.Holded.Contracts;
using Humans.Holded.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Holded.Tests.Services;

/// <summary>
/// The nightly Holded sync must no-op when no API key is configured (PR-preview / local dev),
/// and run the purchase-document sync and the ledger sweep when a key is present.
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

    public enum Phase { Documents, Contacts, Ledger }

    [HumansTheory]
    [Xunit.InlineData(Phase.Documents, Phase.Contacts)]
    [Xunit.InlineData(Phase.Documents, Phase.Ledger)]
    [Xunit.InlineData(Phase.Contacts, Phase.Ledger)]
    public async Task Shutdown_preserves_earlier_failures(Phase failedPhase, Phase canceledPhase)
    {
        using var shutdown = new CancellationTokenSource();
        var holded = Substitute.For<IHoldedService>();
        var failure = new IOException("Earlier phase failed");
        var canceled = new OperationCanceledException(shutdown.Token);
        async Task<bool> RunPhaseAsync(Phase phase)
        {
            if (phase == failedPhase) throw failure;
            if (phase == canceledPhase)
            {
                await shutdown.CancelAsync();
                throw canceled;
            }
            return true;
        }
        holded.SyncPurchaseDocumentsAsync(shutdown.Token).Returns(_ => RunPhaseAsync(Phase.Documents));
        holded.SyncContactsAsync(shutdown.Token).Returns(_ => RunPhaseAsync(Phase.Contacts));
        holded.SyncLedgerAsync(false, shutdown.Token).Returns(_ => RunPhaseAsync(Phase.Ledger));

        var action = () => MakeSync(holded, "k").RunAsync(shutdown.Token);
        var error = await action.Should().ThrowAsync<AggregateException>();
        error.Which.InnerExceptions.Should().Equal(failure, canceled);
        if (canceledPhase == Phase.Contacts)
            await holded.DidNotReceive().SyncLedgerAsync(false, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Independent_phase_failures_are_reported_together(bool ledgerFails)
    {
        var holded = Substitute.For<IHoldedService>();
        var docs = new IOException("Documents failed");
        var contacts = new IOException("Contacts failed");
        var ledger = new IOException("Ledger failed");
        holded.SyncPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(docs);
        holded.SyncContactsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(contacts);
        holded.SyncLedgerAsync(false, Arg.Any<CancellationToken>())
            .Returns(_ => ledgerFails ? Task.FromException<bool>(ledger) : Task.FromResult(true));

        var action = () => MakeSync(holded, "k").RunAsync(Xunit.TestContext.Current.CancellationToken);
        var error = await action.Should().ThrowAsync<AggregateException>();
        error.Which.InnerExceptions.Should().Equal(ledgerFails ? [docs, contacts, ledger] : [docs, contacts]);
        await holded.Received(1).SyncLedgerAsync(false, Xunit.TestContext.Current.CancellationToken);
    }

    [HumansTheory]
    [Xunit.InlineData(Phase.Documents)]
    [Xunit.InlineData(Phase.Contacts)]
    [Xunit.InlineData(Phase.Ledger)]
    public async Task Shutdown_without_an_earlier_failure_preserves_cancellation(Phase canceledPhase)
    {
        using var shutdown = new CancellationTokenSource();
        var holded = Substitute.For<IHoldedService>();
        var canceled = new OperationCanceledException(shutdown.Token);
        async Task<bool> RunPhaseAsync(Phase phase)
        {
            if (phase != canceledPhase) return true;
            await shutdown.CancelAsync();
            throw canceled;
        }
        holded.SyncPurchaseDocumentsAsync(shutdown.Token).Returns(_ => RunPhaseAsync(Phase.Documents));
        holded.SyncContactsAsync(shutdown.Token).Returns(_ => RunPhaseAsync(Phase.Contacts));
        holded.SyncLedgerAsync(false, shutdown.Token).Returns(_ => RunPhaseAsync(Phase.Ledger));

        var action = () => MakeSync(holded, "k").RunAsync(shutdown.Token);
        (await Xunit.Record.ExceptionAsync(action)).Should().BeSameAs(canceled);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Shutdown_between_phases_stops_before_the_next_attempt(bool alreadyCanceled)
    {
        using var shutdown = new CancellationTokenSource();
        var holded = Substitute.For<IHoldedService>();
        if (alreadyCanceled) await shutdown.CancelAsync();
        var failure = new IOException("Document pull failed during shutdown");
        holded.SyncPurchaseDocumentsAsync(shutdown.Token).Returns(async Task<bool> (_) =>
        {
            await shutdown.CancelAsync();
            throw failure;
        });

        var action = () => MakeSync(holded, "k").RunAsync(shutdown.Token);
        var error = await Xunit.Record.ExceptionAsync(action);
        if (alreadyCanceled)
        {
            error.Should().BeOfType<OperationCanceledException>();
            await holded.DidNotReceive().SyncPurchaseDocumentsAsync(Arg.Any<CancellationToken>());
        }
        else
            error.Should().BeOfType<AggregateException>().Which.InnerExceptions.Should().Contain(failure);
        await holded.DidNotReceive().SyncContactsAsync(Arg.Any<CancellationToken>());
        await holded.DidNotReceive().SyncLedgerAsync(false, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RunAsync_SkipsHolded_WhenNoApiKey()
    {
        var holded = Substitute.For<IHoldedService>();
        var logger = new CapturingLogger<HoldedNightlySync>();
        var sync = MakeSync(holded, apiKey: "", logger);

        await sync.RunAsync(Xunit.TestContext.Current.CancellationToken);

        await holded.DidNotReceive().SyncPurchaseDocumentsAsync(Arg.Any<CancellationToken>());
        await holded.DidNotReceive().SyncLedgerAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Information && e.Message.Contains("not configured"));
    }

    [HumansFact]
    public async Task RunAsync_RunsBothSyncs_WhenApiKeyPresent()
    {
        var holded = Substitute.For<IHoldedService>();
        var sync = MakeSync(holded, apiKey: "k");

        await sync.RunAsync(Xunit.TestContext.Current.CancellationToken);

        await holded.Received(1).SyncPurchaseDocumentsAsync(Arg.Any<CancellationToken>());
        await holded.Received(1).SyncLedgerAsync(false, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RunAsync_FailedDocumentSync_StillSweepsTheLedger_ThenFails()
    {
        var holded = Substitute.For<IHoldedService>();
        holded.SyncPurchaseDocumentsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new HoldedTransientException("holded down"));
        holded.SyncLedgerAsync(false, Arg.Any<CancellationToken>()).Returns(true);

        var run = () => MakeSync(holded, apiKey: "k").RunAsync(Xunit.TestContext.Current.CancellationToken);

        await run.Should().ThrowAsync<HoldedTransientException>();
        await holded.Received(1).SyncLedgerAsync(false, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RunAsync_WarnsWhenSweepSkipped()
    {
        var holded = Substitute.For<IHoldedService>();
        holded.SyncPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(true);
        holded.SyncContactsAsync(Arg.Any<CancellationToken>()).Returns(true);
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
        holded.SyncPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(true);
        holded.SyncContactsAsync(Arg.Any<CancellationToken>()).Returns(true);
        holded.SyncLedgerAsync(false, Arg.Any<CancellationToken>()).Returns(true);
        var logger = new CapturingLogger<HoldedNightlySync>();
        var sync = MakeSync(holded, apiKey: "k", logger);

        await sync.RunAsync(Xunit.TestContext.Current.CancellationToken);

        logger.Entries.Should().BeEmpty();
    }
}
