using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Models.Mailer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Mailer;

/// <summary>
/// Acceptance criteria #5 / #7: §5 pairing surfaces non-primary subscribers
/// with their user's current primary, and the snapshot build does not query
/// the database (audience compute uses cached interfaces, name/email reads
/// route through cached UserInfo).
/// </summary>
public class MailerAudienceDebugSnapshotBuilderTests
{
    [HumansFact]
    public async Task Build_NonPrimaryPairing_PairsSubscribedEmailWithUserPrimary()
    {
        // Frank: primary = frank@nobodies.team; ML subscribed under frank@gmail.com.
        // Both are verified rows on the same UserInfo.
        var frankId = Guid.NewGuid();
        var frank = MakeUserInfo(frankId, "Frank", primary: "frank@nobodies.team",
            otherVerified: ["frank@gmail.com"]);

        var audience = StubAudience("ticket-no-shifts", "Humans - Ticket no Shifts",
            members: [frankId]);
        var ml = StubMl(
            groups: [new MailerLiteGroup("g1", "Humans - Ticket no Shifts", Instant.FromUtc(2026, 1, 1, 0, 0), 1, 0, 0, 0, 0)],
            subscribers: [
                new MailerLiteSubscriber("sub-1", "frank@gmail.com", "active",
                    Source: "manual",
                    SubscribedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
                    UnsubscribedAt: null,
                    OptedInAt: null,
                    FirstName: "Frank", LastName: null,
                    GroupIds: ["g1"]),
            ]);
        var users = StubUsers([frank]);

        var snap = await MailerAudienceDebugSnapshotBuilder.BuildAsync(audience, ml, users, NullLogger.Instance, CancellationToken.None);

        snap.NonPrimary.Should().ContainSingle();
        var row = snap.NonPrimary[0];
        row.UserId.Should().Be(frankId);
        row.SubscribedEmail.Should().Be("frank@gmail.com");
        row.PrimaryEmail.Should().Be("frank@nobodies.team");

        // Frank is expected (primary email) but ML has him under gmail → §3 adds primary, §4 removes gmail.
        snap.ToAdd.Should().ContainSingle(r => r.Email == "frank@nobodies.team");
        snap.ToRemove.Should().ContainSingle(r => r.Email == "frank@gmail.com");
    }

    [HumansFact]
    public async Task Build_NoDbQueries_OnlyCachedUserInfoAndMlReads()
    {
        // Sanity: the builder only takes IMailerAudience / IMailerLiteService /
        // IUserServiceRead — no DB context, no email service, no preference service.
        // Reflection confirms the parameter surface to lock the criterion.
        var method = typeof(MailerAudienceDebugSnapshotBuilder)
            .GetMethod(nameof(MailerAudienceDebugSnapshotBuilder.BuildAsync))!;
        var paramTypes = method.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().ContainInOrder(
            typeof(IMailerAudience),
            typeof(IMailerLiteService),
            typeof(IUserServiceRead),
            typeof(ILogger),
            typeof(CancellationToken));
        paramTypes.Should().HaveCount(5,
            "the debug snapshot must reach Humans-side state only via cached IUserServiceRead + a logger — no IUserEmailService, no DbContext, no preference service.");

        // Also exercise the path so we catch any later regression that
        // sneaks a DB-touching service in via a side door.
        var audience = StubAudience("k", "Humans - k", members: []);
        var ml = StubMl(groups: [], subscribers: []);
        var users = StubUsers([]);
        var snap = await MailerAudienceDebugSnapshotBuilder.BuildAsync(audience, ml, users, NullLogger.Instance, CancellationToken.None);
        snap.Expected.Should().BeEmpty();
        snap.CurrentlyInMl.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Build_Expected_UsesPrimaryEmailFromCachedUserInfo()
    {
        var uid = Guid.NewGuid();
        var u = MakeUserInfo(uid, "Alice", primary: "alice@example.com");

        var audience = StubAudience("k", "Humans - k", members: [uid]);
        var ml = StubMl(groups: [], subscribers: []);
        var users = StubUsers([u]);

        var snap = await MailerAudienceDebugSnapshotBuilder.BuildAsync(audience, ml, users, NullLogger.Instance, CancellationToken.None);

        snap.Expected.Should().ContainSingle();
        snap.Expected[0].UserId.Should().Be(uid);
        snap.Expected[0].Email.Should().Be("alice@example.com");
        snap.Expected[0].Name.Should().Be("Alice");
    }

    [HumansFact]
    public async Task Build_CurrentlyInMl_SkipsSuppressedStatuses()
    {
        // Subscribers with status unsubscribed/bounced/junk are filtered by
        // MailerAudienceSyncService and must be filtered here too so the
        // diff preview doesn't lie about what Apply will do.
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var carolId = Guid.NewGuid();
        var darioId = Guid.NewGuid();

        var alice = MakeUserInfo(aliceId, "Alice", primary: "alice@example.com");
        var bob = MakeUserInfo(bobId, "Bob", primary: "bob@example.com");
        var carol = MakeUserInfo(carolId, "Carol", primary: "carol@example.com");
        var dario = MakeUserInfo(darioId, "Dario", primary: "dario@example.com");

        static MailerLiteSubscriber Sub(string id, string email, string status) =>
            new(id, email, status, "manual",
                SubscribedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
                UnsubscribedAt: null, OptedInAt: null,
                FirstName: null, LastName: null,
                GroupIds: ["g1"]);

        var audience = StubAudience("k", "Humans - k", members: [aliceId]);
        var ml = StubMl(
            groups: [new MailerLiteGroup("g1", "Humans - k", Instant.FromUtc(2026, 1, 1, 0, 0), 4, 0, 0, 0, 0)],
            subscribers: [
                Sub("s-alice", "alice@example.com", "active"),
                Sub("s-bob", "bob@example.com", "unsubscribed"),
                Sub("s-carol", "carol@example.com", "bounced"),
                Sub("s-dario", "dario@example.com", "junk"),
            ]);
        var users = StubUsers([alice, bob, carol, dario]);

        var snap = await MailerAudienceDebugSnapshotBuilder.BuildAsync(audience, ml, users, NullLogger.Instance, CancellationToken.None);

        snap.CurrentlyInMl.Should().ContainSingle().Which.Email.Should().Be("alice@example.com");
        snap.ToRemove.Should().BeEmpty("suppressed-status subscribers must not appear as removable");
        snap.ToAdd.Should().BeEmpty("Alice is already in §2 and is the only expected member");
    }

    [HumansFact]
    public async Task Paging_SlicesPagesIndependentlyPerSection()
    {
        var rows = Enumerable.Range(0, 150)
            .Select(i => new DebugExpectedRow(Guid.NewGuid(), $"User {i:D3}", $"user{i:D3}@example.com"))
            .ToList();
        var options = new DebugTableOptions(PageSizes: [50, 100, 200], DefaultPageSize: 50);

        var page1 = MailerAudienceDebugSnapshotBuilder.PageExpected(rows,
            new DebugTableState("exp", 1, 50, DebugSortColumn.Name, Descending: false), options);
        var page2 = MailerAudienceDebugSnapshotBuilder.PageExpected(rows,
            new DebugTableState("exp", 2, 50, DebugSortColumn.Name, Descending: false), options);
        var page3 = MailerAudienceDebugSnapshotBuilder.PageExpected(rows,
            new DebugTableState("exp", 3, 50, DebugSortColumn.Name, Descending: false), options);

        page1.Rows.Should().HaveCount(50);
        page2.Rows.Should().HaveCount(50);
        page3.Rows.Should().HaveCount(50);
        page1.Total.Should().Be(150);
        page1.Rows[0].Name.Should().Be("User 000");
        page2.Rows[0].Name.Should().Be("User 050");
        page3.Rows[0].Name.Should().Be("User 100");
    }

    [HumansFact]
    public void Paging_SortByEmailDescending_ReversesEmailOrder()
    {
        var rows = new List<DebugExpectedRow>
        {
            new(Guid.NewGuid(), "A", "c@x.com"),
            new(Guid.NewGuid(), "B", "a@x.com"),
            new(Guid.NewGuid(), "C", "b@x.com"),
        };
        var options = new DebugTableOptions(PageSizes: [50], DefaultPageSize: 50);

        var ascending = MailerAudienceDebugSnapshotBuilder.PageExpected(rows,
            new DebugTableState("exp", 1, 50, DebugSortColumn.Email, Descending: false), options);
        var descending = MailerAudienceDebugSnapshotBuilder.PageExpected(rows,
            new DebugTableState("exp", 1, 50, DebugSortColumn.Email, Descending: true), options);

        ascending.Rows.Select(r => r.Email).Should().Equal("a@x.com", "b@x.com", "c@x.com");
        descending.Rows.Select(r => r.Email).Should().Equal("c@x.com", "b@x.com", "a@x.com");
    }

    [HumansFact]
    public void Paging_InvalidPageSize_FallsBackToDefault()
    {
        var rows = Enumerable.Range(0, 30)
            .Select(i => new DebugExpectedRow(Guid.NewGuid(), $"User {i:D2}", $"u{i:D2}@x.com"))
            .ToList();
        var options = new DebugTableOptions(PageSizes: [50, 100, 200], DefaultPageSize: 50);

        var page = MailerAudienceDebugSnapshotBuilder.PageExpected(rows,
            new DebugTableState("exp", 1, 99, DebugSortColumn.Name, Descending: false), options);

        page.State.PageSize.Should().Be(50, "99 is not one of the allowed page sizes");
    }

    [HumansFact]
    public void Paging_PageBeyondLast_ClampsToLastPage()
    {
        var rows = Enumerable.Range(0, 30)
            .Select(i => new DebugExpectedRow(Guid.NewGuid(), $"User {i:D2}", $"u{i:D2}@x.com"))
            .ToList();
        var options = new DebugTableOptions(PageSizes: [10], DefaultPageSize: 10);

        var page = MailerAudienceDebugSnapshotBuilder.PageExpected(rows,
            new DebugTableState("exp", 99, 10, DebugSortColumn.Name, Descending: false), options);

        page.Rows.Should().HaveCount(10);
        page.Rows.Last().Name.Should().Be("User 29");
    }

    // -- helpers ---------------------------------------------------------------

    private static IMailerAudience StubAudience(string key, string groupName, IEnumerable<Guid> members)
    {
        var set = new HashSet<Guid>(members);
        var audience = Substitute.For<IMailerAudience>();
        audience.Key.Returns(key);
        audience.DisplayName.Returns(key);
        audience.MailerLiteGroupName.Returns(groupName);
        audience.ComputeMemberUserIdsAsync(Arg.Any<CancellationToken>()).Returns(set as IReadOnlySet<Guid>);
        return audience;
    }

    private static IMailerLiteService StubMl(
        IReadOnlyList<MailerLiteGroup> groups,
        IReadOnlyList<MailerLiteSubscriber> subscribers)
    {
        var ml = Substitute.For<IMailerLiteService>();
        ml.ListGroupsAsync(Arg.Any<CancellationToken>()).Returns(groups);
        ml.ListSubscribersAsync(Arg.Any<CancellationToken>()).Returns(ToAsyncEnumerable(subscribers));
        return ml;
    }

    private static IUserService StubUsers(IReadOnlyCollection<UserInfo> all)
    {
        var users = Substitute.For<IUserService>();
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(all));
        return users;
    }

    private static async IAsyncEnumerable<MailerLiteSubscriber> ToAsyncEnumerable(IReadOnlyList<MailerLiteSubscriber> items)
    {
        foreach (var i in items) yield return i;
        await Task.CompletedTask;
    }

    private static UserInfo MakeUserInfo(
        Guid id, string displayName, string primary, IReadOnlyList<string>? otherVerified = null)
    {
        var now = Instant.FromUtc(2026, 1, 1, 0, 0);
        var emails = new List<UserEmail>
        {
            new()
            {
                Id = Guid.NewGuid(),
                UserId = id,
                Email = primary,
                IsVerified = true,
                IsPrimary = true,
                CreatedAt = now,
                UpdatedAt = now,
            },
        };
        foreach (var e in otherVerified ?? [])
        {
            emails.Add(new UserEmail
            {
                Id = Guid.NewGuid(),
                UserId = id,
                Email = e,
                IsVerified = true,
                IsPrimary = false,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        return UserInfo.Create(
            user: new User
            {
                Id = id,
                DisplayName = displayName,
                PreferredLanguage = "en",
                CreatedAt = now,
                GoogleEmailStatus = GoogleEmailStatus.Unknown,
            },
            userEmails: emails,
            eventParticipations: [],
            externalLogins: [],
            profile: null,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
    }
}
