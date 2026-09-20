using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests;

/// <summary>
/// The scoped sync schedulers are where a membership change turns into autonomous Google
/// work. Without credentials that work would run the reconcilers against in-memory stubs
/// and record successful sync-log rows, so nothing may reach Hangfire.
/// </summary>
public sealed class HangfireSyncSchedulerTests
{
    private readonly IBackgroundJobClient _backgroundJobs = Substitute.For<IBackgroundJobClient>();
    private readonly IGoogleDriveActivityClient _googleClient = Substitute.For<IGoogleDriveActivityClient>();

    private HangfireGoogleGroupSyncScheduler GroupScheduler() => new(
        _backgroundJobs, _googleClient, NullLogger<HangfireGoogleGroupSyncScheduler>.Instance);

    private HangfireGoogleDriveAccessSyncScheduler DriveScheduler() => new(
        _backgroundJobs, _googleClient, NullLogger<HangfireGoogleDriveAccessSyncScheduler>.Instance);

    [HumansFact]
    public void GroupScheduler_WithoutCredentials_EnqueuesNothing()
    {
        _googleClient.IsConfigured.Returns(false);
        var scheduler = GroupScheduler();

        scheduler.Enqueue("team-group@example.org");
        scheduler.Schedule("team-group@example.org", TimeSpan.FromMinutes(15), retryAttempt: 1);

        _backgroundJobs.DidNotReceiveWithAnyArgs().Create(default(Job)!, default(IState)!);
    }

    [HumansFact]
    public void GroupScheduler_WithCredentials_EnqueuesReconcileOne()
    {
        _googleClient.IsConfigured.Returns(true);

        GroupScheduler().Enqueue("team-group@example.org");

        _backgroundJobs.Received(1).Create(
            Arg.Is<Job>(j => j.Method.Name == nameof(IGoogleGroupSync.ReconcileOneAsync)),
            Arg.Any<IState>());
    }

    [HumansFact]
    public void DriveScheduler_WithoutCredentials_EnqueuesNothing()
    {
        _googleClient.IsConfigured.Returns(false);

        DriveScheduler().Enqueue("folder-id");

        _backgroundJobs.DidNotReceiveWithAnyArgs().Create(default(Job)!, default(IState)!);
    }

    [HumansFact]
    public void DriveScheduler_WithCredentials_EnqueuesReconcileOne()
    {
        _googleClient.IsConfigured.Returns(true);

        DriveScheduler().Enqueue("folder-id");

        _backgroundJobs.Received(1).Create(
            Arg.Is<Job>(j => j.Method.Name == nameof(IGoogleDriveSync.ReconcileOneAsync)),
            Arg.Any<IState>());
    }
}
