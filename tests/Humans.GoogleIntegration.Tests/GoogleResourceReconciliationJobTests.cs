using Humans.GoogleIntegration.Services;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Jobs;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
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
    private readonly IGoogleSyncManagement _googleSyncService;
    private readonly IGoogleGroupSync _googleGroupSync;
    private readonly IGoogleDriveSync _googleDriveSync;
    private readonly IGoogleDriveActivityClient _googleClient;
    private readonly FakeClock _clock;
    private readonly IHumansMetrics _metrics;
    private readonly INotificationService _notifications;
    private readonly GoogleResourceReconciliationJob _job;

    public GoogleResourceReconciliationJobTests()
    {
        _googleSyncService = Substitute.For<IGoogleSyncManagement>();
        _googleGroupSync = Substitute.For<IGoogleGroupSync>();
        _googleDriveSync = Substitute.For<IGoogleDriveSync>();
        _googleClient = Substitute.For<IGoogleDriveActivityClient>();
        _googleClient.IsConfigured.Returns(true);
        _googleDriveSync.ReconcileAllAsync(SyncAction.Execute, Arg.Any<CancellationToken>()).Returns(new SyncPreviewResult());
        _googleGroupSync.ReconcileAllAsync(SyncAction.Execute, Arg.Any<CancellationToken>()).Returns(new SyncPreviewResult());
        _googleSyncService.CheckGroupSettingsAsync(Arg.Any<CancellationToken>()).Returns(new GroupSettingsDriftResult());
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 9, 2, 0));
        _metrics = TestMetrics.Create();
        _notifications = Substitute.For<INotificationService>();

        _job = new GoogleResourceReconciliationJob(
            _googleSyncService,
            _googleGroupSync,
            _googleDriveSync,
            _googleClient,
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

        await _googleSyncService.DidNotReceiveWithAnyArgs()
            .SyncResourcesByTypeAsync(default, default, default, default);
        await _googleGroupSync.Received(1)
            .ReconcileAllAsync(SyncAction.Execute, Arg.Any<CancellationToken>());
        await _googleDriveSync.Received(1)
            .ReconcileAllAsync(SyncAction.Execute, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ExecuteAsync_WithoutCredentials_SkipsEveryPhaseAndRecordsSkipped()
    {
        _googleClient.IsConfigured.Returns(false);

        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        _metrics.Received(1).RecordJobRun("google_resource_reconciliation", "skipped");
        _metrics.DidNotReceive().RecordJobRun("google_resource_reconciliation", "success");
        await _googleSyncService.DidNotReceiveWithAnyArgs()
            .SyncResourcesByTypeAsync(default, default, default, default);
        await _googleGroupSync.DidNotReceiveWithAnyArgs()
            .ReconcileAllAsync(default);
        await _googleDriveSync.DidNotReceiveWithAnyArgs()
            .ReconcileAllAsync(default);
        await _googleSyncService.DidNotReceiveWithAnyArgs()
            .CheckGroupSettingsAsync();
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
            GoogleIntegrationNotificationSources.GoogleDriftDetected,
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
    public async Task ExecuteAsync_WhenDriveResultHasErrors_RecordsPartialFailureAndNotifiesSyncError()
    {
        _googleSyncService.CheckGroupSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new GroupSettingsDriftResult());
        _googleDriveSync.ReconcileAllAsync(SyncAction.Execute, Arg.Any<CancellationToken>())
            .Returns(new SyncPreviewResult
            {
                Diffs = [new ResourceSyncDiff { ErrorMessage = "Drive API failed" }]
            });

        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        _metrics.Received(1).RecordJobRun("google_resource_reconciliation", "partial_failure");
        _metrics.DidNotReceive().RecordJobRun("google_resource_reconciliation", "success");
        await _notifications.Received(1).SendToRoleAsync(
            GoogleIntegrationNotificationSources.SyncError,
            Arg.Any<NotificationClass>(),
            Arg.Any<NotificationPriority>(),
            Arg.Any<string>(),
            RoleNames.Admin,
            Arg.Is<string?>(b => b != null && b.Contains("Drive access source reconcile")),
            "/Google/Sync",
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task Shutdown_DistinguishesRequestedCancellationFromPhaseTimeout(bool requested)
    {
        using var shutdown = new CancellationTokenSource();
        var failure = new OperationCanceledException(shutdown.Token);
        _googleSyncService.CheckGroupSettingsAsync(Arg.Any<CancellationToken>()).Returns(new GroupSettingsDriftResult());
        _googleDriveSync.ReconcileAllAsync(SyncAction.Execute, shutdown.Token)
            .Returns(async Task<SyncPreviewResult> (_) =>
            {
                if (requested) await shutdown.CancelAsync();
                throw failure;
            });
        var logger = new CapturingLogger<GoogleResourceReconciliationJob>();
        var job = new GoogleResourceReconciliationJob(_googleSyncService, _googleGroupSync, _googleDriveSync,
            _googleClient, _notifications, _metrics, logger, _clock);

        var error = await Xunit.Record.ExceptionAsync(() => job.ExecuteAsync(shutdown.Token));

        if (requested)
        {
            error.Should().BeSameAs(failure);
            logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Exception == null);
            logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
        }
        else
        {
            error.Should().BeNull();
            logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Exception == failure);
        }
        await _googleGroupSync.Received(requested ? 0 : 1).ReconcileAllAsync(SyncAction.Execute, shutdown.Token);
        _metrics.Received(requested ? 0 : 1).RecordJobRun("google_resource_reconciliation", "partial_failure");
        _metrics.DidNotReceive().RecordJobRun("google_resource_reconciliation", "success");
    }

    [HumansFact]
    public async Task Shutdown_ChecksBeforeStartingTheNextPhase()
    {
        using var shutdown = new CancellationTokenSource();
        _googleDriveSync.ReconcileAllAsync(SyncAction.Execute, shutdown.Token)
            .Returns(async _ => { await shutdown.CancelAsync(); return new SyncPreviewResult(); });

        var error = await Xunit.Record.ExceptionAsync(() => _job.ExecuteAsync(shutdown.Token));

        error.Should().BeOfType<OperationCanceledException>();
        await _googleGroupSync.DidNotReceiveWithAnyArgs().ReconcileAllAsync(default);
        _metrics.DidNotReceive().RecordJobRun("google_resource_reconciliation", "success");
    }

    [HumansFact]
    public async Task Shutdown_ReportsEarlierPhaseFailureBeforePropagatingCancellation()
    {
        using var shutdown = new CancellationTokenSource();
        var failure = new OperationCanceledException(shutdown.Token);
        _googleDriveSync.ReconcileAllAsync(SyncAction.Execute, shutdown.Token).ThrowsAsync(new IOException("Drive failed"));
        _googleGroupSync.ReconcileAllAsync(SyncAction.Execute, shutdown.Token)
            .Returns(async Task<SyncPreviewResult> (_) => { await shutdown.CancelAsync(); throw failure; });

        var error = await Xunit.Record.ExceptionAsync(() => _job.ExecuteAsync(shutdown.Token));

        error.Should().BeSameAs(failure);
        _metrics.Received(1).RecordJobRun("google_resource_reconciliation", "partial_failure");
        await _notifications.Received(1).SendToRoleAsync(GoogleIntegrationNotificationSources.SyncError,
            Arg.Any<NotificationClass>(), Arg.Any<NotificationPriority>(), Arg.Any<string>(), RoleNames.Admin,
            Arg.Is<string?>(b => b != null && b.Contains("Drive access source reconcile")),
            "/Google/Sync", Arg.Any<string?>(), CancellationToken.None);
    }

    [HumansFact]
    public async Task Shutdown_FinishesBothNotificationsAfterTheLastPhase()
    {
        using var shutdown = new CancellationTokenSource();
        _googleDriveSync.ReconcileAllAsync(SyncAction.Execute, shutdown.Token).ThrowsAsync(new IOException("Drive failed"));
        _googleSyncService.EnforceInheritedAccessRestrictionsAsync(shutdown.Token).Returns(1);
        _googleSyncService.CheckGroupSettingsAsync(shutdown.Token)
            .Returns(async _ => { await shutdown.CancelAsync(); return new GroupSettingsDriftResult(); });
        _notifications.SendToRoleAsync(Arg.Any<NotificationSourceDefinition>(), Arg.Any<NotificationClass>(),
                Arg.Any<NotificationPriority>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => { call.Arg<CancellationToken>().ThrowIfCancellationRequested(); return Task.CompletedTask; });

        await _job.ExecuteAsync(shutdown.Token);

        _notifications.ReceivedCalls().Should().HaveCount(2);
        _notifications.ReceivedCalls().Should().AllSatisfy(call => call.GetArguments().Last().Should().Be(CancellationToken.None));
        _metrics.Received(1).RecordJobRun("google_resource_reconciliation", "partial_failure");
    }

    [HumansFact]
    public async Task ReturnedGroupErrorsReachTheFailureSummary()
    {
        _googleGroupSync.ReconcileAllAsync(SyncAction.Execute, Arg.Any<CancellationToken>())
            .Returns(new SyncPreviewResult { Diffs = [new ResourceSyncDiff { ErrorMessage = "Group refused" }] });
        _googleSyncService.CheckGroupSettingsAsync(Arg.Any<CancellationToken>()).Returns(new GroupSettingsDriftResult());

        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        _metrics.Received(1).RecordJobRun("google_resource_reconciliation", "partial_failure");
        _notifications.ReceivedCalls().Should().ContainSingle(call =>
            Equals(call.GetArguments()[0], GoogleIntegrationNotificationSources.SyncError)
            && ((string)call.GetArguments()[5]!).Contains("Group membership reconcile"));
    }

    [HumansFact]
    public async Task ReturnedSettingsCheckErrorsReachTheFailureSummary()
    {
        _googleSyncService.CheckGroupSettingsAsync(Arg.Any<CancellationToken>()).Returns(new GroupSettingsDriftResult
        {
            Reports = [new() { ErrorMessage = "Check failed" }]
        });

        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        _metrics.Received(1).RecordJobRun("google_resource_reconciliation", "partial_failure");
        _notifications.ReceivedCalls().Should().ContainSingle(call =>
            Equals(call.GetArguments()[0], GoogleIntegrationNotificationSources.SyncError)
            && ((string)call.GetArguments()[5]!).Contains("Group settings check"));
    }

    [HumansFact]
    public async Task SettingsSummaryCountsSuccessfulRepairsAndReportsFailures()
    {
        _googleSyncService.CheckGroupSettingsAsync(Arg.Any<CancellationToken>()).Returns(new GroupSettingsDriftResult
        {
            Reports = [Drift("fixed@example.org"), Drift("failed@example.org")]
        });
        _googleSyncService.RemediateGroupSettingsAsync("fixed@example.org", Arg.Any<CancellationToken>())
            .Returns(GroupSettingsRemediationResult.Success());
        _googleSyncService.RemediateGroupSettingsAsync("failed@example.org", Arg.Any<CancellationToken>())
            .Returns(GroupSettingsRemediationResult.Failure("Repair refused"));

        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        _metrics.Received(1).RecordJobRun("google_resource_reconciliation", "partial_failure");
        _metrics.DidNotReceive().RecordJobRun("google_resource_reconciliation", "success");
        var drift = _notifications.ReceivedCalls().Single(call => Equals(call.GetArguments()[0], GoogleIntegrationNotificationSources.GoogleDriftDetected));
        ((string)drift.GetArguments()[3]!).Should().Be("Google reconciliation fixed 1 drift issue(s)");
        var failure = _notifications.ReceivedCalls().Single(call => Equals(call.GetArguments()[0], GoogleIntegrationNotificationSources.SyncError));
        ((string)failure.GetArguments()[5]!).Should().Contain("Group settings check");
    }

    [HumansFact]
    public async Task Shutdown_ReportsCompletedRepairsAndStopsTheNextGroup()
    {
        using var shutdown = new CancellationTokenSource();
        _googleSyncService.CheckGroupSettingsAsync(shutdown.Token).Returns(new GroupSettingsDriftResult
        {
            Reports = [Drift("fixed@example.org"), Drift("next@example.org")]
        });
        _googleSyncService.RemediateGroupSettingsAsync("fixed@example.org", shutdown.Token)
            .Returns(async _ => { await shutdown.CancelAsync(); return GroupSettingsRemediationResult.Success(); });

        var error = await Xunit.Record.ExceptionAsync(() => _job.ExecuteAsync(shutdown.Token));

        error.Should().BeOfType<OperationCanceledException>();
        await _googleSyncService.DidNotReceive().RemediateGroupSettingsAsync("next@example.org", Arg.Any<CancellationToken>());
        var drift = _notifications.ReceivedCalls().Single(call => Equals(call.GetArguments()[0], GoogleIntegrationNotificationSources.GoogleDriftDetected));
        ((string)drift.GetArguments()[3]!).Should().Be("Google reconciliation fixed 1 drift issue(s)");
        drift.GetArguments()[^1].Should().Be(CancellationToken.None);
        _metrics.DidNotReceive().RecordJobRun("google_resource_reconciliation", "success");
        _metrics.DidNotReceive().RecordJobRun("google_resource_reconciliation", "partial_failure");
    }

    private static GroupSettingsDriftReport Drift(string email) => new()
    {
        GroupEmail = email,
        Drifts = [new GroupSettingDrift("setting", "expected", "actual")]
    };
}
