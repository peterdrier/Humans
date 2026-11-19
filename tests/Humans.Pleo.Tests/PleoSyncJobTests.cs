using AwesomeAssertions;
using Humans.Pleo.Jobs;
using Humans.Pleo.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Pleo.Tests;

public sealed class PleoSyncJobTests
{
    private readonly IPleoAdminService _pleo = Substitute.For<IPleoAdminService>();

    private Task RunAsync() => new PleoSyncJob(_pleo, NullLogger<PleoSyncJob>.Instance).ExecuteAsync();

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Cancellation_propagates_and_only_shutdown_logs_a_warning(bool shutdownRequested)
    {
        using var shutdown = new CancellationTokenSource();
        if (shutdownRequested) await shutdown.CancelAsync();
        var canceled = new OperationCanceledException(shutdown.Token);
        _pleo.SyncAsync(shutdown.Token).ThrowsAsync(canceled);
        var logger = new CapturingLogger<PleoSyncJob>();
        var action = () => new PleoSyncJob(_pleo, logger).ExecuteAsync(shutdown.Token);

        (await Xunit.Record.ExceptionAsync(action)).Should().BeSameAs(canceled);
        if (shutdownRequested)
        {
            var entry = logger.Entries.Should().ContainSingle().Subject;
            entry.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Warning);
            entry.Exception.Should().BeNull();
        }
        else
            logger.Entries.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Permanent_failure_completes_the_job_so_Hangfire_does_not_retry()
    {
        _pleo.SyncAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new PleoPermanentException("403", 403));

        await RunAsync();
    }

    [HumansFact]
    public async Task Transient_failure_propagates_so_Hangfire_retries()
    {
        _pleo.SyncAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new PleoTransientException("503"));

        await FluentActions.Awaiting(RunAsync).Should().ThrowAsync<PleoTransientException>();
    }
}
