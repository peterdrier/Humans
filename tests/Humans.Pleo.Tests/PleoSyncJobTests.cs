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
