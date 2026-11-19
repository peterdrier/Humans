using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Humans.Base.Interfaces;
using Humans.Google.Contracts;
using Humans.Google.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Google.Tests;

public sealed class ProcessGoogleSyncOutboxJobTests
{
    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task Shutdown_JobDistinguishesRequestedCancellationFromDependencyFailure(bool requested)
    {
        using var shutdown = new CancellationTokenSource();
        var outbox = Substitute.For<IGoogleSyncOutboxProcessor>();
        var googleClient = Substitute.For<IGoogleDriveActivityClient>();
        var metrics = Substitute.For<IHumansMetrics>();
        var logger = new CapturingLogger<ProcessGoogleSyncOutboxJob>();
        var failure = new OperationCanceledException(shutdown.Token);
        outbox.ProcessQueuedAsync(shutdown.Token).Returns(async _ =>
        {
            if (requested) await shutdown.CancelAsync();
            throw failure;
        });
        var job = new ProcessGoogleSyncOutboxJob(outbox, googleClient, metrics, logger);

        var error = await Xunit.Record.ExceptionAsync(() => job.ExecuteAsync(shutdown.Token));

        error.Should().BeSameAs(failure);
        metrics.Received(requested ? 0 : 1).RecordJobRun("process_google_sync_outbox", "failure");
        metrics.DidNotReceive().RecordJobRun("process_google_sync_outbox", "success");
        metrics.DidNotReceive().RecordJobRun("process_google_sync_outbox", "skipped");
        if (requested)
        {
            logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Exception == null);
            logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
        }
        else
            logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Exception == failure);
    }

    [HumansFact]
    public async Task ExecuteAsync_WithoutCredentials_ReportsSkipped()
    {
        var outbox = Substitute.For<IGoogleSyncOutboxProcessor>();
        var googleClient = Substitute.For<IGoogleDriveActivityClient>();
        googleClient.IsConfigured.Returns(false);
        var metrics = Substitute.For<IHumansMetrics>();
        var job = new ProcessGoogleSyncOutboxJob(
            outbox,
            googleClient,
            metrics,
            NullLogger<ProcessGoogleSyncOutboxJob>.Instance);

        await job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        metrics.Received(1).RecordJobRun("process_google_sync_outbox", "skipped");
        metrics.DidNotReceive().RecordJobRun("process_google_sync_outbox", "success");
    }
}
