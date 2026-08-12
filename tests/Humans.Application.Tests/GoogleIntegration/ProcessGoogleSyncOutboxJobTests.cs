using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories.GoogleIntegration;
using Humans.Infrastructure.Services;

namespace Humans.Application.Tests.GoogleIntegration;

public class ProcessGoogleSyncOutboxJobTests : IDisposable
{
    private readonly GoogleIntegrationDbContext _dbContext;

    /// <summary>
    /// Users stay on the main pile after the GoogleIntegration peel
    /// (nobodies-collective/Humans#858); no test here seeds one, so an empty
    /// context is enough for the display-name stub.
    /// </summary>
    private readonly UsersDbContext _usersDb;

    private readonly IGoogleSyncOutboxRepository _outboxRepository;
    private readonly IGoogleResourceRepository _resourceRepository;
    private readonly IUserService _userService;
    private readonly ITeamService _teamService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly FakeClock _clock;
    private readonly HumansMetricsService _metrics;
    private readonly ProcessGoogleSyncOutboxJob _job;

    public ProcessGoogleSyncOutboxJobTests()
    {
        var options = new DbContextOptionsBuilder<GoogleIntegrationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new GoogleIntegrationDbContext(options);
        var factory = new SingleContextFactory(options);
        _outboxRepository = new GoogleSyncOutboxRepository(factory);
        _resourceRepository = Substitute.For<IGoogleResourceRepository>();
        _resourceRepository
            .GetActiveByTeamIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _userService = Substitute.For<IUserService>();
        _usersDb = new UsersDbContext(
            new DbContextOptionsBuilder<UsersDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        _userService.StubGetUserInfosFromContext(_usersDb);
        _teamService = Substitute.For<ITeamService>();
        _teamService
            .GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        _googleSyncService = Substitute.For<IGoogleSyncService>();
        _clock = new FakeClock(Instant.FromUtc(2026, 2, 15, 20, 0));
        _metrics = TestMetrics.Create();
        var logger = Substitute.For<ILogger<ProcessGoogleSyncOutboxJob>>();

        _job = new ProcessGoogleSyncOutboxJob(
            _outboxRepository,
            _resourceRepository,
            _userService,
            _teamService,
            _googleSyncService,
            _metrics,
            _clock,
            logger);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _usersDb.Dispose();
        _metrics.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task ExecuteAsync_AddUserEvent_ProcessesAndMarksAsCompleted()
    {
        var outboxEvent = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.AddUserToTeamResources);

        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        await _googleSyncService.Received(1).AddUserToTeamResourcesAsync(
            outboxEvent.TeamId,
            outboxEvent.UserId,
            Arg.Any<CancellationToken>());

        var updatedEvent = await _dbContext.GoogleSyncOutboxEvents.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        updatedEvent.ProcessedAt.Should().Be(_clock.GetCurrentInstant());
        updatedEvent.RetryCount.Should().Be(0);
        updatedEvent.LastError.Should().BeNull();
    }

    [HumansFact]
    public async Task ExecuteAsync_RemoveUserFailure_IncrementsRetryAndStoresError()
    {
        var outboxEvent = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.RemoveUserFromTeamResources);

        _googleSyncService
            .When(s => s.RemoveUserFromTeamResourcesAsync(
                outboxEvent.TeamId,
                outboxEvent.UserId,
                Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("google timeout"));

        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        var updatedEvent = await _dbContext.GoogleSyncOutboxEvents.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        updatedEvent.ProcessedAt.Should().BeNull();
        updatedEvent.RetryCount.Should().Be(1);
        updatedEvent.LastError.Should().Contain("google timeout");
    }

    [HumansFact]
    public async Task ExecuteAsync_FinalFailure_RecordsLastErrorAndExhaustsRetries()
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

        await _job.ExecuteAsync(Xunit.TestContext.Current.CancellationToken);

        var updatedEvent = await _dbContext.GoogleSyncOutboxEvents.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        updatedEvent.RetryCount.Should().Be(10);
        updatedEvent.LastError.Should().Contain("google timeout");
        updatedEvent.FailedPermanently.Should().BeTrue();
        updatedEvent.ProcessedAt.Should().Be(_clock.GetCurrentInstant());
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
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        return outboxEvent;
    }

    private sealed class SingleContextFactory(DbContextOptions<GoogleIntegrationDbContext> options)
        : IDbContextFactory<GoogleIntegrationDbContext>
    {
        public GoogleIntegrationDbContext CreateDbContext() => new(options);

        public Task<GoogleIntegrationDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new GoogleIntegrationDbContext(options));
    }
}
