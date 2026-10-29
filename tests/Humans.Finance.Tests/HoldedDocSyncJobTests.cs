using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Finance.Jobs;
using Humans.Finance.Services;
using Humans.Holded.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Finance.Tests;

/// <summary>
/// Finance owns its purchase-doc sync's schedule: the nightly job runs the doc sync, no-ops with
/// an info log when no Holded API key is configured (PR-preview / local dev), and lets a failure
/// reach Hangfire.
/// </summary>
public class HoldedDocSyncJobTests
{
    private readonly IHoldedFinanceManagement _finance = Substitute.For<IHoldedFinanceManagement>();
    private readonly CapturingLogger<HoldedDocSyncJob> _logger = new();

    private HoldedDocSyncJob MakeJob(string apiKey) =>
        new(_finance, Options.Create(new HoldedClientOptions { ApiKey = apiKey }), _logger);

    [HumansFact]
    public async Task ExecuteAsync_SkipsWithInfoLog_WhenNoApiKey()
    {
        await MakeJob(apiKey: " ").ExecuteAsync(TestContext.Current.CancellationToken);

        await _finance.DidNotReceive().SyncAsync(Arg.Any<CancellationToken>());
        _logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Information && e.Message.Contains("not configured"));
    }

    [HumansFact]
    public async Task ExecuteAsync_RunsDocSync_WhenApiKeyPresent()
    {
        var ct = TestContext.Current.CancellationToken;

        await MakeJob(apiKey: "k").ExecuteAsync(ct);

        await _finance.Received(1).SyncAsync(ct);
        _logger.Entries.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ExecuteAsync_Propagates_SyncFailure()
    {
        _finance.SyncAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("down"));

        var act = () => MakeJob(apiKey: "k").ExecuteAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public void Job_IsScheduledNightly_ByFinance()
    {
        ((ISectionJobs)new Section()).Jobs(Substitute.For<IServiceProvider>())
            .Should().ContainSingle(j => j.JobType == typeof(HoldedDocSyncJob))
            .Which.Should().Be(new RecurringJobDescriptor("holded-doc-sync", typeof(HoldedDocSyncJob), "45 2 * * *"));
    }

    /// <summary>The job is internal; Hangfire only needs its ExecuteAsync to be public.</summary>
    [HumansFact]
    public void InternalJob_IsAcceptedByHangfire()
    {
        var job = Hangfire.Common.Job.FromExpression<HoldedDocSyncJob>(j => j.ExecuteAsync(CancellationToken.None));

        job.Type.Should().Be(typeof(HoldedDocSyncJob));
    }
}
