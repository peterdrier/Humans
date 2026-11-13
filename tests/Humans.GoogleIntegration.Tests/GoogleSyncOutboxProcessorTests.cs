using Humans.GoogleIntegration.Jobs;
using Humans.GoogleIntegration.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Teams.Contracts;
using Humans.GoogleIntegration.Data;
using Humans.Base.Interfaces;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Services;
using Humans.GoogleIntegration.Tests.Infrastructure;
using Humans.Users.Contracts;

namespace Humans.GoogleIntegration.Tests;

public class GoogleSyncOutboxProcessorTests : IDisposable
{
    private readonly GoogleIntegrationDbContext _dbContext;

    private readonly IGoogleSyncOutboxRepository _outboxRepository;
    private readonly IUserService _userService;
    private readonly IWorkspaceIdentityManagement _identities;
    private readonly ITeamService _teamService;
    private readonly IGoogleSyncManagement _googleSyncService;
    private readonly IGoogleDriveActivityClient _googleClient;
    private readonly FakeClock _clock;
    private readonly IHumansMetrics _metrics;
    private readonly GoogleSyncOutboxProcessor _processor;

    public GoogleSyncOutboxProcessorTests()
    {
        var options = new DbContextOptionsBuilder<GoogleIntegrationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new GoogleIntegrationDbContext(options);
        var factory = new SingleContextFactory(options);
        _outboxRepository = new GoogleSyncOutboxRepository(factory);
        _userService = Substitute.For<IUserService>();
        _identities = Substitute.For<IWorkspaceIdentityManagement>();
        // No test here seeds a user; the drain only reads this lookup to name the human in
        // its failure log, so an empty result is the whole fixture.
        _userService
            .GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>()));
        _teamService = Substitute.For<ITeamService>();
        _teamService
            .GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        _googleSyncService = Substitute.For<IGoogleSyncManagement>();
        _googleSyncService.AddUserToTeamResourcesAsync(Arg.Any<Guid>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>(), Arg.Any<GoogleSyncSource>())
            .Returns(call => new GoogleResourceGrantResult(GoogleResourceGrantOutcome.Deferred, call.ArgAt<Guid>(1)));
        _googleClient = Substitute.For<IGoogleDriveActivityClient>();
        _googleClient.IsConfigured.Returns(true);
        _clock = new FakeClock(Instant.FromUtc(2026, 2, 15, 20, 0));
        _metrics = TestMetrics.Create();
        var logger = Substitute.For<ILogger<GoogleSyncOutboxProcessor>>();

        _processor = new GoogleSyncOutboxProcessor(
            _outboxRepository,
            _userService, _identities,
            _teamService,
            _googleSyncService,
            _googleClient,
            _metrics,
            _clock,
            logger);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task Shutdown_StatusDependencyCancellationStillRecordsARetry()
    {
        using var shutdown = new CancellationTokenSource();
        var first = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.AddUserToTeamResources);
        _clock.Advance(Duration.FromSeconds(1));
        var next = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.AddUserToTeamResources);
        var failure = new OperationCanceledException("Status save timed out");
        _googleSyncService.AddUserToTeamResourcesAsync(first.TeamId, first.UserId, shutdown.Token,
                Arg.Any<GoogleSyncSource>())
            .Returns(new GoogleResourceGrantResult(GoogleResourceGrantOutcome.Accepted, first.UserId, "member@example.org"));
        _identities.SetStatusFromSyncAsync(first.UserId, "member@example.org", GoogleEmailStatus.Valid, CancellationToken.None)
            .Returns(async Task<bool> (_) => { await shutdown.CancelAsync(); throw failure; });

        var error = await Xunit.Record.ExceptionAsync(() => _processor.ProcessQueuedAsync(shutdown.Token));

        error.Should().BeOfType<OperationCanceledException>();
        var stored = await _dbContext.GoogleSyncOutboxEvents.AsNoTracking()
            .SingleAsync(e => e.Id == first.Id, Xunit.TestContext.Current.CancellationToken);
        stored.ProcessedAt.Should().BeNull();
        stored.RetryCount.Should().Be(1);
        stored.LastError.Should().Be(failure.Message);
        _metrics.Received(1).RecordSyncOperation("failure");
        await _googleSyncService.DidNotReceive().AddUserToTeamResourcesAsync(next.TeamId, next.UserId,
            Arg.Any<CancellationToken>(), Arg.Any<GoogleSyncSource>());
    }

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task Shutdown_FailureTrackingCancellationRemainsAJobFailure(bool permanent)
    {
        using var shutdown = new CancellationTokenSource();
        var first = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.AddUserToTeamResources);
        var repository = Substitute.For<IGoogleSyncOutboxRepository>();
        repository.GetProcessingBatchAsync(100, 10, shutdown.Token).Returns(new[] { first });
        var vendorFailure = new Google.GoogleApiException("drive", "vendor refused")
        {
            Error = new Google.Apis.Requests.RequestError { Code = permanent ? 403 : 503 }
        };
        _googleSyncService.AddUserToTeamResourcesAsync(first.TeamId, first.UserId, shutdown.Token,
                Arg.Any<GoogleSyncSource>())
            .Returns(Task.FromException<GoogleResourceGrantResult>(vendorFailure));
        var failure = new OperationCanceledException("Failure tracking timed out");
        repository.MarkPermanentlyFailedAsync(first.Id, Arg.Any<Instant>(), Arg.Any<string>(), CancellationToken.None)
            .Returns(async _ => { await shutdown.CancelAsync(); throw failure; });
        repository.IncrementRetryAsync(first.Id, Arg.Any<Instant>(), Arg.Any<string>(), 10, CancellationToken.None)
            .Returns(async Task<(bool, int)> (_) => { await shutdown.CancelAsync(); throw failure; });
        var processor = new GoogleSyncOutboxProcessor(repository, _userService, _identities, _teamService,
            _googleSyncService, _googleClient, _metrics, _clock, Substitute.For<ILogger<GoogleSyncOutboxProcessor>>());
        var logger = new CapturingLogger<ProcessGoogleSyncOutboxJob>();
        var job = new ProcessGoogleSyncOutboxJob(processor, _googleClient, _metrics, logger);

        var error = await Xunit.Record.ExceptionAsync(() => job.ExecuteAsync(shutdown.Token));

        error.Should().BeOfType<InvalidOperationException>().Which.InnerException.Should().BeSameAs(failure);
        _metrics.Received(1).RecordJobRun("process_google_sync_outbox", "failure");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Exception == error);
    }

    [HumansFact]
    public async Task ProcessQueuedAsync_FailedGrant_RemainsPending()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var outboxEvent = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.AddUserToTeamResources);
        _googleSyncService.AddUserToTeamResourcesAsync(outboxEvent.TeamId, outboxEvent.UserId,
                Arg.Any<CancellationToken>(), Arg.Any<GoogleSyncSource>())
            .Returns(new GoogleResourceGrantResult(GoogleResourceGrantOutcome.Failed, outboxEvent.UserId,
                Retryable: true, ErrorMessage: "HTTP 503 — vendor unavailable"));

        await _processor.ProcessQueuedAsync(ct);

        var pending = await _outboxRepository.GetProcessingBatchAsync(10, 10, ct);
        pending.Should().ContainSingle().Which.Id.Should().Be(outboxEvent.Id);
        pending[0].RetryCount.Should().Be(1);
        _metrics.DidNotReceive().RecordSyncOperation("success");
    }

    [HumansTheory]
    [Xunit.InlineData(GoogleResourceGrantOutcome.Accepted, false)]
    [Xunit.InlineData(GoogleResourceGrantOutcome.Deferred, false)]
    [Xunit.InlineData(GoogleResourceGrantOutcome.Failed, false)]
    [Xunit.InlineData(GoogleResourceGrantOutcome.Failed, true)]
    public async Task ProcessQueuedAsync_CancelledAfterGrant_PersistsOutcomeBeforeStopping(
        GoogleResourceGrantOutcome outcome, bool retryable)
    {
        using var shutdown = new CancellationTokenSource();
        var first = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.AddUserToTeamResources);
        _clock.Advance(Duration.FromSeconds(1));
        var next = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.AddUserToTeamResources);
        _googleSyncService.AddUserToTeamResourcesAsync(first.TeamId, first.UserId,
                shutdown.Token, Arg.Any<GoogleSyncSource>())
            .Returns(async _ =>
            {
                await shutdown.CancelAsync();
                return new GoogleResourceGrantResult(outcome, first.UserId, "member@example.org",
                    Retryable: retryable, ErrorMessage: "grant refused");
            });

        var act = () => _processor.ProcessQueuedAsync(shutdown.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var stored = await _dbContext.GoogleSyncOutboxEvents.AsNoTracking()
            .SingleAsync(e => e.Id == first.Id, Xunit.TestContext.Current.CancellationToken);
        var failed = outcome == GoogleResourceGrantOutcome.Failed;
        stored.ProcessedAt.Should().Be(failed && retryable ? null : _clock.GetCurrentInstant());
        stored.FailedPermanently.Should().Be(failed && !retryable);
        stored.RetryCount.Should().Be(failed && retryable ? 1 : 0);
        stored.LastError.Should().Be(failed ? "grant refused" : null);
        await _identities.Received(outcome == GoogleResourceGrantOutcome.Accepted ? 1 : 0)
            .SetStatusFromSyncAsync(first.UserId, "member@example.org", GoogleEmailStatus.Valid,
                CancellationToken.None);
        await _googleSyncService.DidNotReceive().AddUserToTeamResourcesAsync(next.TeamId, next.UserId,
            Arg.Any<CancellationToken>(), Arg.Any<GoogleSyncSource>());
        var untouched = await _dbContext.GoogleSyncOutboxEvents.AsNoTracking()
            .SingleAsync(e => e.Id == next.Id, Xunit.TestContext.Current.CancellationToken);
        untouched.ProcessedAt.Should().BeNull();
        untouched.RetryCount.Should().Be(0);
        untouched.LastError.Should().BeNull();
    }

    [HumansTheory]
    [Xunit.InlineData(System.Net.HttpStatusCode.Forbidden)]
    [Xunit.InlineData(System.Net.HttpStatusCode.ServiceUnavailable)]
    public async Task ProcessQueuedAsync_CancelledAfterVendorError_PersistsFailureBeforeStopping(
        System.Net.HttpStatusCode status)
    {
        using var shutdown = new CancellationTokenSource();
        var first = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.AddUserToTeamResources);
        _clock.Advance(Duration.FromSeconds(1));
        var next = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.AddUserToTeamResources);
        var error = new Google.GoogleApiException("drive", "vendor refused")
        {
            Error = new Google.Apis.Requests.RequestError { Code = (int)status }
        };
        _googleSyncService.AddUserToTeamResourcesAsync(first.TeamId, first.UserId,
                shutdown.Token, Arg.Any<GoogleSyncSource>())
            .Returns(async _ =>
            {
                await shutdown.CancelAsync();
                return await Task.FromException<GoogleResourceGrantResult>(error);
            });

        var act = () => _processor.ProcessQueuedAsync(shutdown.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var stored = await _dbContext.GoogleSyncOutboxEvents.AsNoTracking()
            .SingleAsync(e => e.Id == first.Id, Xunit.TestContext.Current.CancellationToken);
        var permanent = status == System.Net.HttpStatusCode.Forbidden;
        stored.FailedPermanently.Should().Be(permanent);
        stored.ProcessedAt.Should().Be(permanent ? _clock.GetCurrentInstant() : null);
        stored.RetryCount.Should().Be(permanent ? 0 : 1);
        stored.LastError.Should().Be(error.Message);
        await _googleSyncService.DidNotReceive().AddUserToTeamResourcesAsync(next.TeamId, next.UserId,
            Arg.Any<CancellationToken>(), Arg.Any<GoogleSyncSource>());
    }

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task ProcessQueuedAsync_PreflightCancellation_OnlyRequestedCancellationEscapes(bool callerAborted)
    {
        using var shutdown = new CancellationTokenSource();
        var first = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.AddUserToTeamResources);
        _googleSyncService.AddUserToTeamResourcesAsync(first.TeamId, first.UserId,
                shutdown.Token, Arg.Any<GoogleSyncSource>())
            .Returns(async _ =>
            {
                if (callerAborted)
                    await shutdown.CancelAsync();
                return await Task.FromException<GoogleResourceGrantResult>(new OperationCanceledException(shutdown.Token));
            });

        var act = () => _processor.ProcessQueuedAsync(shutdown.Token);

        if (callerAborted)
            await act.Should().ThrowAsync<OperationCanceledException>();
        else
            await act.Should().NotThrowAsync();
        var stored = await _dbContext.GoogleSyncOutboxEvents.AsNoTracking()
            .SingleAsync(Xunit.TestContext.Current.CancellationToken);
        stored.ProcessedAt.Should().BeNull();
        stored.FailedPermanently.Should().BeFalse();
        stored.RetryCount.Should().Be(callerAborted ? 0 : 1);
        _metrics.Received(callerAborted ? 0 : 1).RecordSyncOperation("failure");
        _metrics.DidNotReceive().RecordSyncOperation("success");
    }

    [HumansFact]
    public async Task ProcessQueuedAsync_PermanentGrantFailure_RecordsFailureWithoutRejectingAGuessedAddress()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var outboxEvent = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.AddUserToTeamResources);
        _googleSyncService.AddUserToTeamResourcesAsync(outboxEvent.TeamId, outboxEvent.UserId,
                Arg.Any<CancellationToken>(), Arg.Any<GoogleSyncSource>())
            .Returns(new GoogleResourceGrantResult(GoogleResourceGrantOutcome.Failed, outboxEvent.UserId,
                ErrorMessage: "HTTP 403 — service account lacks access"));

        await _processor.ProcessQueuedAsync(ct);

        (await _outboxRepository.GetProcessingBatchAsync(10, 10, ct)).Should().BeEmpty();
        _metrics.Received(1).RecordSyncOperation("permanent_failure");
        _metrics.DidNotReceive().RecordSyncOperation("success");
        await _identities.DidNotReceiveWithAnyArgs().SetStatusFromSyncAsync(default, default!, default);
        var stored = await _dbContext.GoogleSyncOutboxEvents.AsNoTracking().SingleAsync(ct);
        stored.LastError.Should().Be("HTTP 403 — service account lacks access");
    }

    [HumansFact]
    public async Task ProcessQueuedAsync_AddUserEvent_ProcessesAndMarksAsCompleted()
    {
        var outboxEvent = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.AddUserToTeamResources);

        await _processor.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        await _googleSyncService.Received(1).AddUserToTeamResourcesAsync(
            outboxEvent.TeamId,
            outboxEvent.UserId,
            Arg.Any<CancellationToken>(),
            GoogleSyncSource.TeamMemberJoined);

        var updatedEvent = await _dbContext.GoogleSyncOutboxEvents.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        updatedEvent.ProcessedAt.Should().Be(_clock.GetCurrentInstant());
        updatedEvent.RetryCount.Should().Be(0);
        updatedEvent.LastError.Should().BeNull();
    }

    [HumansFact]
    public async Task ProcessQueuedAsync_AdminResyncEvent_RecordsManualSource()
    {
        var outboxEvent = await SeedOutboxEventAsync(
            GoogleSyncOutboxEventTypes.AddUserToTeamResources,
            deduplicationKey: $"admin-resync:{Guid.NewGuid()}");

        await _processor.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        await _googleSyncService.Received(1).AddUserToTeamResourcesAsync(
            outboxEvent.TeamId,
            outboxEvent.UserId,
            Arg.Any<CancellationToken>(),
            GoogleSyncSource.ManualSync);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ProcessQueuedAsync_EmailStatusFailure_RemainsPendingUntilTailSucceeds(bool grantFails)
    {
        var outboxEvent = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.AddUserToTeamResources);
        var fail = true;
        var failure = new DbUpdateException("Email status persistence unavailable");
        _googleSyncService.AddUserToTeamResourcesAsync(outboxEvent.TeamId, outboxEvent.UserId,
                Arg.Any<CancellationToken>(), Arg.Any<GoogleSyncSource>())
            .Returns(_ => fail && grantFails ? throw failure : new GoogleResourceGrantResult(GoogleResourceGrantOutcome.Accepted, outboxEvent.UserId, "member@example.org"));
        _identities.SetStatusFromSyncAsync(
                outboxEvent.UserId, Arg.Any<string>(), GoogleEmailStatus.Valid, Arg.Any<CancellationToken>())
            .Returns(_ => fail ? throw failure : Task.FromResult(true));

        await _processor.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        var pending = await _outboxRepository.GetProcessingBatchAsync(10, 10, Xunit.TestContext.Current.CancellationToken);
        pending.Should().ContainSingle().Which.Id.Should().Be(outboxEvent.Id);
        pending[0].RetryCount.Should().Be(1);
        pending[0].LastError.Should().Be(failure.Message);
        _metrics.DidNotReceive().RecordSyncOperation("success");

        fail = false;
        await _processor.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        var stored = await _dbContext.GoogleSyncOutboxEvents.AsNoTracking()
            .SingleAsync(Xunit.TestContext.Current.CancellationToken);
        stored.ProcessedAt.Should().Be(_clock.GetCurrentInstant());
        stored.LastError.Should().BeNull();
        _metrics.Received(1).RecordSyncOperation("success");
        await _identities.Received(grantFails ? 1 : 2).SetStatusFromSyncAsync(
            outboxEvent.UserId, Arg.Any<string>(), GoogleEmailStatus.Valid, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ProcessQueuedAsync_AccountLinkResyncEvent_RecordsManualSource()
    {
        // The key shape ITeamWrite.EnqueueGoogleResyncForUserTeamsAsync writes when an
        // admin links a Workspace account.
        var outboxEvent = await SeedOutboxEventAsync(
            GoogleSyncOutboxEventTypes.AddUserToTeamResources,
            deduplicationKey: $"{Guid.NewGuid()}:{GoogleSyncOutboxEventTypes.AddUserToTeamResources}:resync:{_clock.GetCurrentInstant()}");

        await _processor.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        await _googleSyncService.Received(1).AddUserToTeamResourcesAsync(
            outboxEvent.TeamId,
            outboxEvent.UserId,
            Arg.Any<CancellationToken>(),
            GoogleSyncSource.ManualSync);
    }

    [HumansFact]
    public async Task ProcessQueuedAsync_WithoutCredentials_LeavesEventsPending()
    {
        _googleClient.IsConfigured.Returns(false);
        var outboxEvent = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.AddUserToTeamResources);
        await _processor.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        var pending = await _dbContext.GoogleSyncOutboxEvents
            .AsNoTracking()
            .SingleAsync(Xunit.TestContext.Current.CancellationToken);
        pending.ProcessedAt.Should().BeNull();
        pending.RetryCount.Should().Be(0);
        pending.FailedPermanently.Should().BeFalse();
        await _googleSyncService.DidNotReceiveWithAnyArgs()
            .AddUserToTeamResourcesAsync(default, default);
        await _identities.DidNotReceiveWithAnyArgs()
            .SetStatusFromSyncAsync(default, default!, default);
    }

    [HumansFact]
    public async Task ProcessQueuedAsync_RemoveUserEvent_CompletesWithoutCallingGoogle()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.RemoveUserFromTeamResources);

        await _processor.ProcessQueuedAsync(ct);

        _googleSyncService.ReceivedCalls().Should().BeEmpty();
        (await _outboxRepository.GetProcessingBatchAsync(10, 10, ct)).Should().BeEmpty();
        var stored = await _dbContext.GoogleSyncOutboxEvents.AsNoTracking().SingleAsync(ct);
        stored.FailedPermanently.Should().BeFalse();
    }

    [HumansFact]
    public async Task ProcessQueuedAsync_GrantThrows_IncrementsRetryAndStoresError()
    {
        var outboxEvent = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.AddUserToTeamResources);

        _googleSyncService.AddUserToTeamResourcesAsync(outboxEvent.TeamId, outboxEvent.UserId,
                Arg.Any<CancellationToken>(), Arg.Any<GoogleSyncSource>())
            .Returns(Task.FromException<GoogleResourceGrantResult>(new InvalidOperationException("google timeout")));

        await _processor.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        var updatedEvent = await _dbContext.GoogleSyncOutboxEvents.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        updatedEvent.ProcessedAt.Should().BeNull();
        updatedEvent.RetryCount.Should().Be(1);
        updatedEvent.LastError.Should().Contain("google timeout");
    }

    [HumansFact]
    public async Task ProcessQueuedAsync_FinalFailure_RecordsLastErrorAndExhaustsRetries()
    {
        var outboxEvent = await SeedOutboxEventAsync(
            GoogleSyncOutboxEventTypes.AddUserToTeamResources,
            retryCount: 9);

        _googleSyncService.AddUserToTeamResourcesAsync(outboxEvent.TeamId, outboxEvent.UserId,
                Arg.Any<CancellationToken>(), Arg.Any<GoogleSyncSource>())
            .Returns(Task.FromException<GoogleResourceGrantResult>(new InvalidOperationException("google timeout")));

        await _processor.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        var updatedEvent = await _dbContext.GoogleSyncOutboxEvents.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        updatedEvent.RetryCount.Should().Be(10);
        updatedEvent.LastError.Should().Contain("google timeout");
        updatedEvent.FailedPermanently.Should().BeTrue();
        updatedEvent.ProcessedAt.Should().Be(_clock.GetCurrentInstant());
    }

    private async Task<GoogleSyncOutboxEvent> SeedOutboxEventAsync(
        string eventType,
        int retryCount = 0,
        string? deduplicationKey = null)
    {
        var outboxEvent = new GoogleSyncOutboxEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            TeamId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            OccurredAt = _clock.GetCurrentInstant(),
            RetryCount = retryCount,
            DeduplicationKey = deduplicationKey ?? $"{Guid.NewGuid()}:{eventType}"
        };

        _dbContext.GoogleSyncOutboxEvents.Add(outboxEvent);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        return outboxEvent;
    }

    [HumansTheory]
    [Xunit.InlineData(GoogleResourceGrantOutcome.Deferred)]
    [Xunit.InlineData(GoogleResourceGrantOutcome.Failed)]
    [Xunit.InlineData(GoogleResourceGrantOutcome.Accepted)]
    public async Task ProcessQueuedAsync_OnlyAcceptedGrantMarksEmailValid(GoogleResourceGrantOutcome outcome)
    {
        var outboxEvent = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.AddUserToTeamResources);
        _googleSyncService.AddUserToTeamResourcesAsync(outboxEvent.TeamId, outboxEvent.UserId,
                Arg.Any<CancellationToken>(), Arg.Any<GoogleSyncSource>()).Returns(new GoogleResourceGrantResult(outcome, outboxEvent.UserId, "member@example.org"));

        await _processor.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        await _identities.Received(outcome == GoogleResourceGrantOutcome.Accepted ? 1 : 0)
            .SetStatusFromSyncAsync(outboxEvent.UserId, Arg.Any<string>(), GoogleEmailStatus.Valid, Arg.Any<CancellationToken>());
        (await _dbContext.GoogleSyncOutboxEvents.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken))
            .ProcessedAt.Should().NotBeNull();
    }

    private sealed class SingleContextFactory(DbContextOptions<GoogleIntegrationDbContext> options)
        : IDbContextFactory<GoogleIntegrationDbContext>
    {
        public GoogleIntegrationDbContext CreateDbContext() => new(options);

        public Task<GoogleIntegrationDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new GoogleIntegrationDbContext(options));
    }

    [HumansFact]
    public async Task ProcessQueuedAsync_AddUserEventForAMergedAwayId_MarksTheSurvivorsEmailValid()
    {
        // The sync carried the event out for the survivor, so the status lands there.
        var outboxEvent = await SeedOutboxEventAsync(GoogleSyncOutboxEventTypes.AddUserToTeamResources);
        var survivorId = Guid.NewGuid();
        _userService
            .GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>
                {
                    [outboxEvent.UserId] = UserInfo.Create(
                        new User { Id = survivorId, UserName = "s", Email = "s@example.org" },
                        [], [], null),
                }));
        _googleSyncService.AddUserToTeamResourcesAsync(outboxEvent.TeamId, outboxEvent.UserId,
                Arg.Any<CancellationToken>(), Arg.Any<GoogleSyncSource>())
            .Returns(new GoogleResourceGrantResult(GoogleResourceGrantOutcome.Accepted, survivorId, "member@example.org"));

        await _processor.ProcessQueuedAsync(Xunit.TestContext.Current.CancellationToken);

        await _identities.Received(1).SetStatusFromSyncAsync(
            survivorId, Arg.Any<string>(), GoogleEmailStatus.Valid, Arg.Any<CancellationToken>());
        await _identities.DidNotReceive().SetStatusFromSyncAsync(
            outboxEvent.UserId, Arg.Any<string>(), Arg.Any<GoogleEmailStatus>(), Arg.Any<CancellationToken>());
    }
}
