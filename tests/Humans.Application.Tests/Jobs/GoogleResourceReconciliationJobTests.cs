using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Jobs;
using Xunit;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Notifications;

namespace Humans.Application.Tests.Jobs;

public class GoogleResourceReconciliationJobTests : IDisposable
{
    private readonly IGoogleSyncService _googleSyncService;
    private readonly FakeClock _clock;
    private readonly IJobRunMetrics _metrics;
    private readonly GoogleResourceReconciliationJob _job;

    public GoogleResourceReconciliationJobTests()
    {
        _googleSyncService = Substitute.For<IGoogleSyncService>();
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 9, 2, 0));
        _metrics = Substitute.For<IJobRunMetrics>();

        _job = new GoogleResourceReconciliationJob(
            _googleSyncService,
            Substitute.For<INotificationService>(),
            _metrics,
            NullLogger<GoogleResourceReconciliationJob>.Instance,
            _clock);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ExecuteAsync_SyncsEveryLinkableResourceType()
    {
        _googleSyncService.CheckGroupSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new GroupSettingsDriftResult());

        await _job.ExecuteAsync();

        await _googleSyncService.Received(1)
            .SyncResourcesByTypeAsync(GoogleResourceType.DriveFolder, SyncAction.Execute, Arg.Any<CancellationToken>());
        await _googleSyncService.Received(1)
            .SyncResourcesByTypeAsync(GoogleResourceType.DriveFile, SyncAction.Execute, Arg.Any<CancellationToken>());
        await _googleSyncService.Received(1)
            .SyncResourcesByTypeAsync(GoogleResourceType.Group, SyncAction.Execute, Arg.Any<CancellationToken>());
    }
}
