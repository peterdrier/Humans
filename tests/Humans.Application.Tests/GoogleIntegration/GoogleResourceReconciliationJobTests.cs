using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.DTOs;
using Humans.Domain.Enums;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Notifications;

namespace Humans.Application.Tests.GoogleIntegration;

public class GoogleResourceReconciliationJobTests : IDisposable
{
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IGoogleGroupSync _googleGroupSync;
    private readonly FakeClock _clock;
    private readonly HumansMetricsService _metrics;
    private readonly GoogleResourceReconciliationJob _job;

    public GoogleResourceReconciliationJobTests()
    {
        _googleSyncService = Substitute.For<IGoogleSyncService>();
        _googleGroupSync = Substitute.For<IGoogleGroupSync>();
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 9, 2, 0));
        _metrics = new HumansMetricsService(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<HumansMetricsService>>());

        _job = new GoogleResourceReconciliationJob(
            _googleSyncService,
            _googleGroupSync,
            Substitute.For<INotificationService>(),
            _metrics,
            NullLogger<GoogleResourceReconciliationJob>.Instance,
            _clock);
    }

    public void Dispose()
    {
        _metrics.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task ExecuteAsync_SyncsDriveResourcesAndReconcilesGroupMembership()
    {
        _googleSyncService.CheckGroupSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new GroupSettingsDriftResult());

        await _job.ExecuteAsync();

        await _googleSyncService.Received(1)
            .SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute, Arg.Any<CancellationToken>());
        await _googleSyncService.Received(1)
            .SyncResourcesByTypeAsync(GoogleResourceType.DriveFile, SyncAction.Execute, Arg.Any<CancellationToken>());
        await _googleSyncService.DidNotReceive()
            .SyncResourcesByTypeAsync(GoogleResourceType.Group, Arg.Any<SyncAction>(), Arg.Any<CancellationToken>());
        await _googleGroupSync.Received(1)
            .ReconcileAllAsync(SyncAction.Execute, Arg.Any<CancellationToken>());
    }
}
