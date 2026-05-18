using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Services.Mailer;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Mailer;

public class MailerAudienceSyncServiceTests
{
    private readonly IMailerLiteService _ml = Substitute.For<IMailerLiteService>();
    private readonly IUserEmailService _emails = Substitute.For<IUserEmailService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();

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

        var result = await NewService(audience).SyncAsync(audience, ct: CancellationToken.None);

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

        var result = await NewService(audience).SyncAsync(audience, ct: CancellationToken.None);

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

        var result = await NewService(audience).SyncAsync(audience, ct: CancellationToken.None);

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

        var result = await NewService(audience).SyncAsync(audience, ct: CancellationToken.None);

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

        await NewService(audience).SyncAsync(audience, ct: CancellationToken.None);

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

        var result = await NewService(audience).SyncAsync(audience, ct: CancellationToken.None);

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
            .Returns<Task>(_ => throw new HttpRequestException("simulated 500"));

        var result = await NewService(audience).SyncAsync(audience, ct: CancellationToken.None);

        result.Errors.Should().Be(1);
        result.Assigned.Should().Be(1); // s2 still succeeded
    }

    [HumansFact]
    public async Task SyncAsync_GroupNameLacksPrefix_ThrowsBeforeAnyMlCall()
    {
        var audience = NewAudience("a-aud", "Newsletter", []);

        var act = async () => await NewService(audience).SyncAsync(audience, ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Humans - *");
    }

    [HumansFact]
    public async Task SyncAsync_WritesAuditEntryWithSerializedCounts()
    {
        var userA = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", [userA]);
        SetupEmails((userA, "a@example.com"));
        SetupGroups(Group("g1", "Humans - A"));
        SetupSubscribers(Subscriber("s1", "a@example.com", "active"));

        await NewService(audience).SyncAsync(audience, ct: CancellationToken.None);

        await _audit.Received(1).LogAsync(
            AuditAction.MailerLiteAudienceSyncCompleted,
            "MailerAudience",
            Guid.Empty,
            Arg.Is<string>(d => d.Contains("\"audience_key\":\"a-aud\"")
                             && d.Contains("\"assigned\":1")),
            Arg.Any<string>(),
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    // ---------- helpers ----------

    private MailerAudienceSyncService NewService(params IMailerAudience[] audiences) => new(
        _ml, _emails, _audit, audiences,
        NullLogger<MailerAudienceSyncService>.Instance);

    private static IMailerAudience NewAudience(
        string key, string groupName, IEnumerable<Guid> members)
    {
        var mock = Substitute.For<IMailerAudience>();
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
