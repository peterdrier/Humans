using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Jobs;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Tests.Infrastructure;
using Humans.Notifications.Contracts;

namespace Humans.GoogleIntegration.Tests;

public class GoogleResourceReconciliationJobTests : IDisposable
{
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IGoogleGroupSync _googleGroupSync;
    private readonly FakeClock _clock;
    private readonly IHumansMetrics _metrics;
    private readonly INotificationService _notifications;
    private readonly GoogleResourceReconciliationJob _job;

    public GoogleResourceReconciliationJobTests()
    {
        _googleSyncService = Substitute.For<IGoogleSyncService>();
        _googleGroupSync = Substitute.For<IGoogleGroupSync>();
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 9, 2, 0));
        _metrics = TestMetrics.Create();
        _notifications = Substitute.For<INotificationService>();

        _job = new GoogleResourceReconciliationJob(
            _googleSyncService,
            _googleGroupSync,
            _notifications,
            _metrics,
            NullLogger<GoogleResourceReconciliationJob>.Instance,
            _clock);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task ExecuteAsync_SyncsDriveResourcesAndReconcilesGroupMembership()
    {
        _googleSyncService.CheckGroupSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new GroupSettingsDriftResult());

        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        await _googleSyncService.Received(1)
            .SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute, Arg.Any<CancellationToken>());
        await _googleSyncService.Received(1)
            .SyncResourcesByTypeAsync(GoogleResourceType.DriveFile, SyncAction.Execute, Arg.Any<CancellationToken>());
        await _googleSyncService.DidNotReceive()
            .SyncResourcesByTypeAsync(GoogleResourceType.Group, Arg.Any<SyncAction>(), Arg.Any<CancellationToken>());
        await _googleGroupSync.Received(1)
            .ReconcileAllAsync(SyncAction.Execute, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_WhenDriftCorrected_LinksAdminsToTheSyncPage()
    {
        _googleSyncService.EnforceInheritedAccessRestrictionsAsync(Arg.Any<CancellationToken>())
            .Returns(1);
        _googleSyncService.CheckGroupSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new GroupSettingsDriftResult());

        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        await _notifications.Received(1).SendToRoleAsync(
            NotificationSource.GoogleDriftDetected,
            Arg.Any<NotificationClass>(),
            Arg.Any<NotificationPriority>(),
            Arg.Any<string>(),
            RoleNames.Admin,
            Arg.Any<string?>(),
            "/Google/Sync",
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_PropagatesCancellation_InsteadOfTreatingItAsPhaseFailure()
    {
        _googleSyncService.SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        Func<Task> act = () => _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<OperationCanceledException>();

        await _googleGroupSync.DidNotReceive()
            .ReconcileAllAsync(Arg.Any<SyncAction>(), Arg.Any<CancellationToken>());
    }
}
