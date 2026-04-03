using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Application.Tests.Jobs;

public class ProcessGoogleSyncOutboxJobTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly INotificationService _notificationService;
    private readonly FakeClock _clock;
    private readonly HumansMetricsService _metrics;
    private readonly ProcessGoogleSyncOutboxJob _job;

    public ProcessGoogleSyncOutboxJobTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _googleSyncService = Substitute.For<IGoogleSyncService>();
        _notificationService = Substitute.For<INotificationService>();
        _clock = new FakeClock(Instant.FromUtc(2026, 2, 15, 20, 0));
        _metrics = new HumansMetricsService(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<HumansMetricsService>>());
        var logger = Substitute.For<ILogger<ProcessGoogleSyncOutboxJob>>();

        _job = new ProcessGoogleSyncOutboxJob(_dbContext, _googleSyncService, _notificationService, _metrics, _clock, logger);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _metrics.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ExecuteAsync_AddUserEvent_ProcessesAndMarksAsCompleted()
    {
        var outboxEvent = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.AddUserToTeamResources);

        await _job.ExecuteAsync();

        await _googleSyncService.Received(1).AddUserToTeamResourcesAsync(
            outboxEvent.TeamId,
            outboxEvent.UserId,
            Arg.Any<CancellationToken>());

        var updatedEvent = await _dbContext.GoogleSyncOutboxEvents.SingleAsync();
        updatedEvent.ProcessedAt.Should().Be(_clock.GetCurrentInstant());
        updatedEvent.RetryCount.Should().Be(0);
        updatedEvent.LastError.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_RemoveUserFailure_IncrementsRetryAndStoresError()
    {
        var outboxEvent = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.RemoveUserFromTeamResources);

        _googleSyncService
            .When(s => s.RemoveUserFromTeamResourcesAsync(
                outboxEvent.TeamId,
                outboxEvent.UserId,
                Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("google timeout"));

        await _job.ExecuteAsync();

        var updatedEvent = await _dbContext.GoogleSyncOutboxEvents.SingleAsync();
        updatedEvent.ProcessedAt.Should().BeNull();
        updatedEvent.RetryCount.Should().Be(1);
        updatedEvent.LastError.Should().Contain("google timeout");
    }

    [Fact]
    public async Task ExecuteAsync_FinalFailure_SendsAdminNotificationToGoogleSyncDashboard()
    {
        var outboxEvent = await SeedOutboxEventAsync(
            GoogleSyncOutboxEventTypes.RemoveUserFromTeamResources,
            retryCount: 9);

        _googleSyncService
            .When(s => s.RemoveUserFromTeamResourcesAsync(
                outboxEvent.TeamId,
                outboxEvent.UserId,
                Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("google timeout"));

        await _job.ExecuteAsync();

        await _notificationService.Received(1).SendToRoleAsync(
            NotificationSource.SyncError,
            NotificationClass.Actionable,
            NotificationPriority.High,
            "Google sync event failed after all retries",
            RoleNames.Admin,
            Arg.Any<string?>(),
            "/Google/Sync",
            "View \u2192",
            Arg.Any<CancellationToken>());
    }

    private async Task<GoogleSyncOutboxEvent> SeedOutboxEventAsync(string eventType, int retryCount = 0)
    {
        var outboxEvent = new GoogleSyncOutboxEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            TeamId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            OccurredAt = _clock.GetCurrentInstant(),
            RetryCount = retryCount,
            DeduplicationKey = $"{Guid.NewGuid()}:{eventType}"
        };

        _dbContext.GoogleSyncOutboxEvents.Add(outboxEvent);
        await _dbContext.SaveChangesAsync();
        return outboxEvent;
    }
}
