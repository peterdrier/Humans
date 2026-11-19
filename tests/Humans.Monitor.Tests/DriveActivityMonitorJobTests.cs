using Microsoft.Extensions.Logging;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Monitor.Contracts;
using Humans.Monitor.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Monitor.Tests;

/// <summary>
/// The job is the only thing that turns a scan outcome into something Hangfire and Prometheus
/// can see, so what it records — and that it lets a total-outage throw through — is the
/// invariant here.
/// </summary>
public class DriveActivityMonitorJobTests
{
    private readonly IDriveActivityMonitorService _monitorService = Substitute.For<IDriveActivityMonitorService>();
    private readonly IHumansMetrics _metrics = Substitute.For<IHumansMetrics>();
    private readonly DriveActivityMonitorJob _job;

    public DriveActivityMonitorJobTests()
    {
        _job = new DriveActivityMonitorJob(
            _monitorService,
            _metrics,
            NullLogger<DriveActivityMonitorJob>.Instance,
            new FakeClock(Instant.FromUtc(2026, 4, 22, 10, 0)));
    }

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task Shutdown_JobDistinguishesItsTokenFromDependencyCancellation(bool ownToken)
    {
        using var shutdown = new CancellationTokenSource();
        var failure = new OperationCanceledException(ownToken ? shutdown.Token : new CancellationToken(true));
        _monitorService.CheckForAnomalousActivityAsync(shutdown.Token).Returns(async Task<int> (_) =>
        {
            if (ownToken) await shutdown.CancelAsync();
            throw failure;
        });
        var logger = new CapturingLogger<DriveActivityMonitorJob>();
        var job = new DriveActivityMonitorJob(_monitorService, _metrics, logger,
            new FakeClock(Instant.FromUtc(2026, 4, 22, 10, 0)));

        var error = await Xunit.Record.ExceptionAsync(() => job.ExecuteAsync(shutdown.Token));

        error.Should().BeSameAs(failure);
        _metrics.DidNotReceive().RecordJobRun("drive_activity_monitor", "success");
        if (ownToken)
        {
            _metrics.DidNotReceive().RecordJobRun("drive_activity_monitor", "failure");
            logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning && entry.Exception == null);
            logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Error);
        }
        else
        {
            _metrics.Received(1).RecordJobRun("drive_activity_monitor", "failure");
            logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error && entry.Exception == failure);
        }
    }

    [HumansFact]
    public async Task ExecuteAsync_RecordsSuccess_WhenTheScanCompletes()
    {
        _monitorService.CheckForAnomalousActivityAsync(Arg.Any<CancellationToken>()).Returns(3);

        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        _metrics.Received(1).RecordJobRun("drive_activity_monitor", "success");
        _metrics.DidNotReceive().RecordJobRun("drive_activity_monitor", "failure");
    }

    [HumansFact]
    public async Task ExecuteAsync_RecordsFailureAndRethrows_WhenTheScanThrows()
    {
        // The scan throws on a total connector outage. Swallowing it here would leave
        // Hangfire recording a successful run while nothing was actually checked.
        _monitorService.CheckForAnomalousActivityAsync(Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new InvalidOperationException("connector unavailable"));

        var act = async () => await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("connector unavailable");
        _metrics.Received(1).RecordJobRun("drive_activity_monitor", "failure");
        _metrics.DidNotReceive().RecordJobRun("drive_activity_monitor", "success");
    }
}
