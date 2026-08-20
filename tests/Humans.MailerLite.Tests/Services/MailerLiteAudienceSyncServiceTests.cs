using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.MailerLite.Data;
using Humans.MailerLite.Services.Dtos;
using Humans.Users.Contracts;
using Humans.MailerLite.Services;
using Humans.MailerLite.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.MailerLite.Tests.Services;

public class MailerLiteAudienceSyncServiceTests
{
    private static readonly Instant SyncedAt = Instant.FromUtc(2026, 8, 21, 9, 0);

    private readonly IMailerLiteService _ml = Substitute.For<IMailerLiteService>();
    private readonly IUserEmailService _emails = Substitute.For<IUserEmailService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IMailerLiteRepository _repository = InMemoryMailerLiteRepository.New();

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

        var result = await NewService(audience).SyncAsync(audience, ct: Xunit.TestContext.Current.CancellationToken);

        result.Created.Should().Be(1);
        result.Assigned.Should().Be(0);
        result.Unassigned.Should().Be(0);
        await _ml.Received(1).BulkImportSubscribersToGroupAsync(
            "g1", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SyncAsync_UnsubscribedUser_ExcludedFromGroup()
    {
        var userA = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", [userA]);
        SetupEmails((userA, "a@example.com"));
        SetupGroups(Group("g1", "Humans - A"));
        SetupSubscribers(Subscriber("s1", "a@example.com", "unsubscribed"));

        var result = await NewService(audience).SyncAsync(audience, ct: Xunit.TestContext.Current.CancellationToken);

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

        var result = await NewService(audience).SyncAsync(audience, ct: Xunit.TestContext.Current.CancellationToken);

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

        var result = await NewService(audience).SyncAsync(audience, ct: Xunit.TestContext.Current.CancellationToken);

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

        await NewService(audience).SyncAsync(audience, ct: Xunit.TestContext.Current.CancellationToken);

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

        var result = await NewService(audience).SyncAsync(audience, ct: Xunit.TestContext.Current.CancellationToken);

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
        var audience = NewAudience("a-aud", "Humans - A", [userA, userB]);
        SetupEmails((userA, "a@example.com"), (userB, "b@example.com"));
        SetupGroups(Group("g1", "Humans - A"));
        SetupSubscribers(
            Subscriber("s1", "a@example.com", "active"),
            Subscriber("s2", "b@example.com", "active"));
        _ml.AssignSubscriberToGroupAsync("s1", "g1", Arg.Any<CancellationToken>())
            .Returns(_ => throw new HttpRequestException("simulated 500"));

        var result = await NewService(audience).SyncAsync(audience, ct: Xunit.TestContext.Current.CancellationToken);

        result.Errors.Should().Be(1);
        result.Assigned.Should().Be(1); // s2 still succeeded
    }

    [HumansFact]
    public async Task SyncAsync_GroupNameLacksPrefix_ThrowsBeforeAnyMlCall()
    {
        var audience = NewAudience("a-aud", "Newsletter", []);

        var act = async () => await NewService(audience).SyncAsync(audience, ct: Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Humans - *");
    }

    [HumansFact]
    public async Task SyncAsync_PersistsSyncStateAndAuditsInProse()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userA = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", [userA]);
        SetupEmails((userA, "a@example.com"));
        SetupGroups(Group("g1", "Humans - A"));
        SetupSubscribers(Subscriber("s1", "a@example.com", "active"));

        await NewService(audience).SyncAsync(audience, ct: ct);

        var state = await _repository.GetSyncStateAsync("a-aud", ct);
        state.Should().NotBeNull();
        state!.LastSyncAt.Should().Be(SyncedAt);
        state.GroupId.Should().Be("g1");
        state.GroupName.Should().Be("Humans - A");
        state.Candidates.Should().Be(1);
        state.Assigned.Should().Be(1);
        state.Summary.Should().Be("0 created, 1 newly assigned, 0 unassigned, 0 errors.");

        // Prose, not JSON, and pointed at the sync-state row rather than Guid.Empty.
        await _audit.Received(1).LogAsync(
            AuditAction.MailerLiteAudienceSyncCompleted,
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
        var ct = Xunit.TestContext.Current.CancellationToken;
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
        var ct = Xunit.TestContext.Current.CancellationToken;
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
        await _audit.DidNotReceive().GetFilteredEntriesAsync(
            Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
            Arg.Any<IReadOnlyList<AuditAction>?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ---------- helpers ----------

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
        new(id, name, Instant.FromUtc(2026, 1, 1, 0, 0), 0, 0, 0, 0, 0);

    private static MailerLiteSubscriber Subscriber(
        string id, string email, string status, string[]? inGroups = null) =>
        new(id, email, status, "api",
            SubscribedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
            UnsubscribedAt: null, OptedInAt: null,
            FirstName: null, LastName: null,
            GroupIds: inGroups ?? []);
}
