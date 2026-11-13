using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.MailerLite.Data;
using Humans.MailerLite.Domain;
using Humans.MailerLite.Services.Dtos;
using Humans.Users.Contracts;
using Humans.MailerLite.Services;
using Humans.MailerLite.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.MailerLite.Tests.Services;

public class MailerLiteAudienceSyncServiceTests
{
    public enum AudienceWrite
    {
        CreateGroup,
        BulkImport,
        Assign,
        Unassign
    }

    private static readonly Instant SyncedAt = Instant.FromUtc(2026, 8, 21, 9, 0);

    private readonly IMailerLiteService _ml = Substitute.For<IMailerLiteService>();
    private readonly IUserEmailService _emails = Substitute.For<IUserEmailService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IMailerLiteRepository _repository = InMemoryMailerLiteRepository.New();

    public enum DetachedFailure { CreateGroup, Summary }

    [HumansTheory]
    [InlineData(DetachedFailure.CreateGroup, false)]
    [InlineData(DetachedFailure.CreateGroup, true)]
    [InlineData(DetachedFailure.Summary, false)]
    [InlineData(DetachedFailure.Summary, true)]
    public async Task Detached_timeouts_remain_failures_when_shutdown_also_begins(DetachedFailure stage, bool shutdownRequested)
    {
        using var shutdown = new CancellationTokenSource();
        var audience = NewAudience("a-aud", "Humans - A", []);
        SetupEmailsEmpty();
        SetupSubscribers();
        SetupGroups(stage == DetachedFailure.CreateGroup ? [] : [Group("g1", "Humans - A")]);
        var timeout = new TaskCanceledException("Detached dependency timed out");
        _ml.CreateGroupAsync("Humans - A", CancellationToken.None).Returns(async Task<MailerLiteGroup> (_) =>
        {
            if (shutdownRequested) await shutdown.CancelAsync();
            throw timeout;
        });
        var repository = Substitute.For<IMailerLiteRepository>();
        repository.UpsertSyncStateAsync(Arg.Any<MailerLiteSyncState>(), CancellationToken.None)
            .Returns(async Task<MailerLiteSyncState> (_) =>
            {
                if (shutdownRequested) await shutdown.CancelAsync();
                throw timeout;
            });
        var service = new MailerLiteAudienceSyncService(_ml, _emails, _audit, repository, new FakeClock(SyncedAt),
            [audience], NullLogger<MailerLiteAudienceSyncService>.Instance);

        var error = await Record.ExceptionAsync(() => service.SyncAsync(audience, ct: shutdown.Token));
        if (shutdownRequested)
            error.Should().BeOfType<InvalidOperationException>().Which.InnerException.Should().BeSameAs(timeout);
        else
            error.Should().BeSameAs(timeout, "admin calls must retain their existing timeout feedback");
    }

    public enum PriorFailure { Preflight, Operation, OperationAtBoundary }

    [HumansTheory]
    [InlineData(PriorFailure.Preflight)]
    [InlineData(PriorFailure.Operation)]
    [InlineData(PriorFailure.OperationAtBoundary)]
    public async Task Interrupted_sweep_keeps_prior_audience_and_operation_failures(PriorFailure stage)
    {
        using var shutdown = new CancellationTokenSource();
        var user = Guid.NewGuid();
        var first = NewAudience("a-aud", "Humans - A", [user]);
        var next = NewAudience("b-aud", "Humans - B", []);
        SetupEmails((user, "member@example.com"));
        SetupGroups(Group("g1", "Humans - A"));
        SetupSubscribers(Subscriber("s1", "member@example.com", "active"));
        var failure = new IOException("Earlier audience failed");
        var canceled = new OperationCanceledException(shutdown.Token);
        if (stage == PriorFailure.Preflight)
            first.ComputeMemberUserIdsAsync(shutdown.Token).Returns(Task.FromException<IReadOnlySet<Guid>>(failure));
        _ml.AssignSubscriberToGroupAsync("s1", "g1", CancellationToken.None).Returns(async _ =>
        {
            if (stage == PriorFailure.OperationAtBoundary) await shutdown.CancelAsync();
            throw failure;
        });
        next.ComputeMemberUserIdsAsync(shutdown.Token).Returns(async Task<IReadOnlySet<Guid>> (_) =>
        {
            await shutdown.CancelAsync();
            throw canceled;
        });

        var action = () => NewService(first, next).SyncAllAsync(ct: shutdown.Token);
        var error = await action.Should().ThrowAsync<AggregateException>();
        if (stage == PriorFailure.Preflight)
            error.Which.InnerExceptions.Should().Equal(failure, canceled);
        else
        {
            error.Which.Message.Should().Contain("1 subscriber operation");
            var state = await _repository.GetSyncStateAsync(first.Key, TestContext.Current.CancellationToken);
            state!.Errors.Should().Be(1);
        }
        if (stage == PriorFailure.OperationAtBoundary)
            await next.DidNotReceiveWithAnyArgs().ComputeMemberUserIdsAsync(default);
    }

    [HumansTheory]
    [InlineData(AudienceWrite.CreateGroup)]
    [InlineData(AudienceWrite.BulkImport)]
    [InlineData(AudienceWrite.Assign)]
    [InlineData(AudienceWrite.Unassign)]
    public async Task SyncAllAsync_CancelledDuringWrites_FinishesAudienceStateAndAuditBeforeStopping(
        AudienceWrite cancelledDuring)
    {
        using var shutdown = new CancellationTokenSource();
        var existingUser = Guid.NewGuid();
        var newUser = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", [existingUser, newUser]);
        var next = NewAudience("b-aud", "Humans - B", []);
        var createsGroup = cancelledDuring == AudienceWrite.CreateGroup;
        SetupEmails((existingUser, "existing@example.com"), (newUser, "new@example.com"));
        SetupGroups(createsGroup ? [] : [Group("g1", "Humans - A")]);
        SetupSubscribers(Subscriber("existing", "existing@example.com", "active"),
            Subscriber("obsolete", "obsolete@example.com", "active", createsGroup ? [] : ["g1"]));

        async Task ObserveWriteAsync(AudienceWrite operation, CancellationToken token)
        {
            if (cancelledDuring == operation)
                await shutdown.CancelAsync();
            token.ThrowIfCancellationRequested();
        }

        _ml.CreateGroupAsync("Humans - A", Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await ObserveWriteAsync(AudienceWrite.CreateGroup, call.ArgAt<CancellationToken>(1));
            return Group("g1", "Humans - A");
        });
        _ml.BulkImportSubscribersToGroupAsync("g1", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await ObserveWriteAsync(AudienceWrite.BulkImport, call.ArgAt<CancellationToken>(2));
                return new BulkImportResult(1, 0, 0, 0);
            });
        _ml.AssignSubscriberToGroupAsync("existing", "g1", Arg.Any<CancellationToken>()).Returns(async call =>
            await ObserveWriteAsync(AudienceWrite.Assign, call.ArgAt<CancellationToken>(2)));
        _ml.UnassignSubscriberFromGroupAsync("obsolete", "g1", Arg.Any<CancellationToken>()).Returns(async call =>
            await ObserveWriteAsync(AudienceWrite.Unassign, call.ArgAt<CancellationToken>(2)));

        var act = () => NewService(audience, next).SyncAllAsync(ct: shutdown.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var state = await _repository.GetSyncStateAsync(audience.Key, TestContext.Current.CancellationToken);
        state.Should().NotBeNull();
        state!.Created.Should().Be(1);
        state.Assigned.Should().Be(1);
        state.Unassigned.Should().Be(createsGroup ? 0 : 1);
        state.Errors.Should().Be(0);
        await _ml.Received(1).BulkImportSubscribersToGroupAsync("g1", Arg.Any<IReadOnlyList<string>>(), CancellationToken.None);
        await _ml.Received(1).AssignSubscriberToGroupAsync("existing", "g1", CancellationToken.None);
        await _ml.Received(createsGroup ? 0 : 1).UnassignSubscriberFromGroupAsync("obsolete", "g1", CancellationToken.None);
        if (createsGroup)
            await _ml.Received(1).CreateGroupAsync("Humans - A", CancellationToken.None);
        await _audit.Received(1).LogAsync(MailerLiteAuditActions.MailerLiteAudienceSyncCompleted,
            "MailerLiteAudience", state.Id, Arg.Any<string>(), nameof(MailerLiteAudienceSyncService));
        await next.DidNotReceiveWithAnyArgs().ComputeMemberUserIdsAsync(default);
        (await _repository.GetSyncStateAsync(next.Key, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [HumansFact]
    public async Task SyncAllAsync_CancelledAtEndOfPreflight_DoesNotStartWrites()
    {
        using var shutdown = new CancellationTokenSource();
        var user = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", [user]);
        SetupEmails((user, "new@example.com"));
        SetupSubscribers();
        _ml.ListGroupsAsync(shutdown.Token).Returns(async _ =>
        {
            await shutdown.CancelAsync();
            return (IReadOnlyList<MailerLiteGroup>)[];
        });

        var act = () => NewService(audience).SyncAllAsync(ct: shutdown.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _ml.DidNotReceiveWithAnyArgs().CreateGroupAsync(default!);
        await _ml.DidNotReceiveWithAnyArgs().BulkImportSubscribersToGroupAsync(default!, default!);
        (await _repository.GetSyncStatesAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task SyncAllAsync_UnrelatedPreflightTimeout_StillAttemptsNextAudience()
    {
        using var lifetime = new CancellationTokenSource();
        var first = NewAudience("a-aud", "Humans - A", []);
        var next = NewAudience("b-aud", "Humans - B", []);
        first.ComputeMemberUserIdsAsync(lifetime.Token)
            .Returns(Task.FromException<IReadOnlySet<Guid>>(new OperationCanceledException(lifetime.Token)));
        SetupEmailsEmpty();
        SetupGroups(Group("g2", "Humans - B"));
        SetupSubscribers();

        var results = await NewService(first, next).SyncAllAsync(ct: lifetime.Token);

        results.Should().ContainSingle().Which.Key.Should().Be(next.Key);
        await next.Received(1).ComputeMemberUserIdsAsync(lifetime.Token);
    }

    [HumansFact]
    public async Task SyncAsync_NewUserNotInML_BulkImportsAndAssigns()
    {
        var userA = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", [userA]);
        SetupEmails((userA, "a@example.com"));
        SetupGroups(Group("g1", "Humans - A"));
        SetupSubscribers();
        _ml.BulkImportSubscribersToGroupAsync(
                "g1",
                Arg.Is<IReadOnlyList<string>>(l => l.Single() == "a@example.com"),
                Arg.Any<CancellationToken>())
            .Returns(new BulkImportResult(1, 0, 0, 0));

        var result = await NewService(audience).SyncAsync(audience, ct: TestContext.Current.CancellationToken);

        result.Created.Should().Be(1);
        result.Assigned.Should().Be(0);
        result.Unassigned.Should().Be(0);
        await _ml.Received(1).BulkImportSubscribersToGroupAsync(
            "g1", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false, "duplicate@example.com")]
    [InlineData(false, " Duplicate@Example.com ")]
    [InlineData(true, "duplicate@example.com")]
    [InlineData(true, " Duplicate@Example.com ")]
    public async Task SyncAsync_SharedDestination_WritesEachSubscriberOnce(bool existingSubscriber, string secondAddress)
    {
        var source = Guid.NewGuid();
        var survivor = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", [source, survivor]);
        SetupEmails((source, "duplicate@example.com"), (survivor, secondAddress));
        SetupGroups(Group("g1", "Humans - A"));
        SetupSubscribers(existingSubscriber ? [Subscriber("s1", "duplicate@example.com", "active")] : []);
        _ml.BulkImportSubscribersToGroupAsync("g1", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new BulkImportResult(1, 0, 0, 0));

        var result = await NewService(audience).SyncAsync(audience, ct: TestContext.Current.CancellationToken);

        result.Candidates.Should().Be(2);
        result.Errors.Should().Be(0);
        if (existingSubscriber)
        {
            result.Assigned.Should().Be(1);
            await _ml.Received(1).AssignSubscriberToGroupAsync("s1", "g1", Arg.Any<CancellationToken>());
            await _ml.DidNotReceiveWithAnyArgs().BulkImportSubscribersToGroupAsync(default!, default!);
        }
        else
        {
            result.Created.Should().Be(1);
            await _ml.Received(1).BulkImportSubscribersToGroupAsync("g1",
                Arg.Is<IReadOnlyList<string>>(addresses => addresses.Count == 1 && addresses[0] == "duplicate@example.com"),
                Arg.Any<CancellationToken>());
            await _ml.DidNotReceiveWithAnyArgs().AssignSubscriberToGroupAsync(default!, default!);
        }
    }

    // The invariant names three suppressed statuses, so all three are exercised here:
    // testing only "unsubscribed" left bounced/junk pinned nowhere on the apply path.
    [HumansTheory]
    [InlineData("unsubscribed")]
    [InlineData("bounced")]
    [InlineData("junk")]
    public async Task SyncAsync_SuppressedUser_ExcludedFromGroup(string status)
    {
        var userA = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", [userA]);
        SetupEmails((userA, "a@example.com"));
        SetupGroups(Group("g1", "Humans - A"));
        SetupSubscribers(Subscriber("s1", "a@example.com", status));

        var result = await NewService(audience).SyncAsync(audience, ct: TestContext.Current.CancellationToken);

        result.ExcludedUnsubscribed.Should().Be(1);
        result.Created.Should().Be(0);
        result.Assigned.Should().Be(0);
        await _ml.DidNotReceive().AssignSubscriberToGroupAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _ml.DidNotReceive().BulkImportSubscribersToGroupAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SyncAsync_ExistingSubscriberNotInGroup_AssignsIt()
    {
        var userA = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", [userA]);
        SetupEmails((userA, "a@example.com"));
        SetupGroups(Group("g1", "Humans - A"));
        SetupSubscribers(Subscriber("s1", "a@example.com", "active"));

        var result = await NewService(audience).SyncAsync(audience, ct: TestContext.Current.CancellationToken);

        result.Assigned.Should().Be(1);
        await _ml.Received(1).AssignSubscriberToGroupAsync("s1", "g1", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SyncAsync_UserDroppedOut_Unassigned()
    {
        var audience = NewAudience("a-aud", "Humans - A", []);
        SetupEmailsEmpty();
        SetupGroups(Group("g1", "Humans - A"));
        SetupSubscribers(Subscriber("s1", "a@example.com", "active", inGroups: ["g1"]));

        var result = await NewService(audience).SyncAsync(audience, ct: TestContext.Current.CancellationToken);

        result.Unassigned.Should().Be(1);
        await _ml.Received(1).UnassignSubscriberFromGroupAsync("s1", "g1", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SyncAsync_GroupMissing_CreatesItFirst()
    {
        var userA = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", [userA]);
        SetupEmails((userA, "a@example.com"));
        SetupGroups(); // empty
        _ml.CreateGroupAsync("Humans - A", Arg.Any<CancellationToken>())
            .Returns(Group("g1", "Humans - A"));
        SetupSubscribers();
        _ml.BulkImportSubscribersToGroupAsync(
                "g1", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new BulkImportResult(1, 0, 0, 0));

        await NewService(audience).SyncAsync(audience, ct: TestContext.Current.CancellationToken);

        await _ml.Received(1).CreateGroupAsync("Humans - A", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SyncAsync_Idempotent_AllAlreadyAssignedOnSecondRun()
    {
        var userA = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", [userA]);
        SetupEmails((userA, "a@example.com"));
        SetupGroups(Group("g1", "Humans - A"));
        SetupSubscribers(Subscriber("s1", "a@example.com", "active", inGroups: ["g1"]));

        var result = await NewService(audience).SyncAsync(audience, ct: TestContext.Current.CancellationToken);

        result.AlreadyAssigned.Should().Be(1);
        result.Created.Should().Be(0);
        result.Assigned.Should().Be(0);
        result.Unassigned.Should().Be(0);
        await _ml.DidNotReceive().AssignSubscriberToGroupAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _ml.DidNotReceive().UnassignSubscriberFromGroupAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _ml.DidNotReceive().BulkImportSubscribersToGroupAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SyncAsync_AssignFails_CountedInErrorsAndSyncContinues()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var alias = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", [userA, userB, alias]);
        SetupEmails((userA, "a@example.com"), (userB, "b@example.com"), (alias, "A@example.com"));
        SetupGroups(Group("g1", "Humans - A"));
        SetupSubscribers(
            Subscriber("s1", "a@example.com", "active"),
            Subscriber("s2", "b@example.com", "active"));
        _ml.AssignSubscriberToGroupAsync("s1", "g1", Arg.Any<CancellationToken>())
            .Returns(_ => throw new HttpRequestException("simulated 500"));

        var result = await NewService(audience).SyncAsync(audience, ct: TestContext.Current.CancellationToken);

        result.Errors.Should().Be(1);
        await _ml.Received(1).AssignSubscriberToGroupAsync("s1", "g1", Arg.Any<CancellationToken>());
        result.Assigned.Should().Be(1); // s2 still succeeded
    }

    [HumansFact]
    public async Task SyncAsync_GroupNameLacksPrefix_ThrowsBeforeAnyMlCall()
    {
        var audience = NewAudience("a-aud", "Newsletter", []);

        var act = async () => await NewService(audience).SyncAsync(audience, ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Humans - *");
    }

    [HumansFact]
    public async Task SyncAsync_PersistsSyncStateAndAuditsInProse()
    {
        var ct = TestContext.Current.CancellationToken;
        var userA = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", [userA]);
        SetupEmails((userA, "a@example.com"));
        SetupGroups(Group("g1", "Humans - A"));
        SetupSubscribers(Subscriber("s1", "a@example.com", "active"));

        await NewService(audience).SyncAsync(audience, ct: ct);

        var state = await _repository.GetSyncStateAsync("a-aud", ct);
        state.Should().NotBeNull();
        state.LastSyncAt.Should().Be(SyncedAt);
        state.GroupId.Should().Be("g1");
        state.GroupName.Should().Be("Humans - A");
        state.Candidates.Should().Be(1);
        state.Assigned.Should().Be(1);
        state.Summary.Should().Be("0 created, 1 newly assigned, 0 unassigned, 0 errors.");

        // Prose, not JSON, and pointed at the sync-state row rather than Guid.Empty.
        await _audit.Received(1).LogAsync(
            MailerLiteAuditActions.MailerLiteAudienceSyncCompleted,
            "MailerLiteAudience",
            state.Id,
            Arg.Is<string>(d => !d.StartsWith('{')
                             && d.Contains("Humans - A", StringComparison.Ordinal)
                             && d.Contains("1 newly assigned", StringComparison.Ordinal)),
            Arg.Any<string>(),
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SyncAsync_SecondRun_OverwritesTheSameRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var userA = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", [userA]);
        SetupEmails((userA, "a@example.com"));
        SetupGroups(Group("g1", "Humans - A"));
        SetupSubscribers(Subscriber("s1", "a@example.com", "active"));

        var service = NewService(audience);
        await service.SyncAsync(audience, ct: ct);
        var first = await _repository.GetSyncStateAsync("a-aud", ct);

        await service.SyncAsync(audience, ct: ct);
        var states = await _repository.GetSyncStatesAsync(ct);

        states.Should().ContainSingle().Which.Id.Should().Be(first!.Id);
    }

    [HumansFact]
    public async Task ComputeAllStatsAsync_ReadsLastSyncFromTheSyncStateTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var userA = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", [userA]);
        SetupEmails((userA, "a@example.com"));
        SetupGroups(Group("g1", "Humans - A"));
        SetupSubscribers(Subscriber("s1", "a@example.com", "active"));

        var service = NewService(audience);
        var before = await service.ComputeAllStatsAsync(ct);
        before.Single().LastSyncAt.Should().BeNull();

        await service.SyncAsync(audience, ct: ct);

        var after = await service.ComputeAllStatsAsync(ct);
        after.Single().LastSyncAt.Should().Be(SyncedAt);
        after.Single().LastSyncSummary.Should().Be("0 created, 1 newly assigned, 0 unassigned, 0 errors.");
    }

    [HumansFact]
    public async Task ComputeAllStatsAsync_DuplicateKeyRows_ShowsTheNewest()
    {
        var ct = TestContext.Current.CancellationToken;
        var audience = NewAudience("a-aud", "Humans - A", []);
        SetupEmailsEmpty();
        SetupGroups(Group("g1", "Humans - A"));
        SetupSubscribers();
        var newest = Instant.FromUtc(2026, 8, 20, 0, 0);
        var repository = Substitute.For<IMailerLiteRepository>();
        repository.GetSyncStatesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new MailerLiteSyncState { Id = Guid.NewGuid(), Key = "a-aud", LastSyncAt = newest.Minus(Duration.FromDays(1)) },
            new MailerLiteSyncState { Id = Guid.NewGuid(), Key = "a-aud", LastSyncAt = newest },
        ]);
        var service = new MailerLiteAudienceSyncService(
            _ml, _emails, _audit, repository, new FakeClock(SyncedAt), [audience],
            NullLogger<MailerLiteAudienceSyncService>.Instance);

        var stats = await service.ComputeAllStatsAsync(ct);

        stats.Single().LastSyncAt.Should().Be(newest);
    }

    private MailerLiteAudienceSyncService NewService(params IMailerLiteAudience[] audiences) => new(
        _ml, _emails, _audit, _repository, new FakeClock(SyncedAt), audiences,
        NullLogger<MailerLiteAudienceSyncService>.Instance);

    private static IMailerLiteAudience NewAudience(
        string key, string groupName, IEnumerable<Guid> members)
    {
        var mock = Substitute.For<IMailerLiteAudience>();
        mock.Key.Returns(key);
        mock.DisplayName.Returns(key);
        mock.MailerLiteGroupName.Returns(groupName);
        mock.ComputeMemberUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(members.ToHashSet());
        return mock;
    }

    private void SetupEmails(params (Guid UserId, string Email)[] mapping)
    {
        var dict = mapping.ToDictionary(x => x.UserId, x => x.Email);
        _emails.GetNotificationTargetEmailsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(dict);
    }

    private void SetupEmailsEmpty()
    {
        _emails.GetNotificationTargetEmailsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());
    }

    private void SetupGroups(params MailerLiteGroup[] groups)
        => _ml.ListGroupsAsync(Arg.Any<CancellationToken>()).Returns(groups);

    private void SetupSubscribers(params MailerLiteSubscriber[] subscribers)
        => _ml.ListSubscribersAsync(Arg.Any<CancellationToken>())
              .Returns(subscribers.ToAsyncEnumerable());

    private static MailerLiteGroup Group(string id, string name) =>
        new(id, name, 0, 0, 0, 0, 0);

    private static MailerLiteSubscriber Subscriber(
        string id, string email, string status, string[]? inGroups = null) =>
        new(id, email, status, "api",
            SubscribedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
            UnsubscribedAt: null, OptedInAt: null,
            FirstName: null, LastName: null,
            GroupIds: inGroups ?? []);
}
