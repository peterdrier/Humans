using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Camps.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.Governance.Contracts;
using Humans.Notifications.Contracts;
using Humans.Notifications.Services;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Notifications.Tests.Services;

/// <summary>
/// The composition <see cref="NotificationInboxRead"/> does for the machine API: the unread
/// tab of the inbox service, newest first, plus the role-gated meters, highest priority first.
/// The inbox service is a substitute; the meter provider is real over substituted sections.
/// </summary>
public sealed class NotificationInboxReadTests : IDisposable
{
    private readonly INotificationInboxService _inbox = Substitute.For<INotificationInboxService>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly IGoogleSyncServiceRead _google = Substitute.For<IGoogleSyncServiceRead>();
    private readonly ITeamServiceRead _teams = Substitute.For<ITeamServiceRead>();
    private readonly ITicketSync _tickets = Substitute.For<ITicketSync>();
    private readonly IApplicationServiceRead _applications = Substitute.For<IApplicationServiceRead>();
    private readonly ICampServiceRead _camps = Substitute.For<ICampServiceRead>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly NotificationInboxRead _sut;

    public NotificationInboxReadTests()
    {
        _users.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>([]));
        _google.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(new Dictionary<Guid, TeamInfo>()));
        _tickets.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(false);
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new CampSettingsInfo(2026, []));
        _camps.GetCampsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CampInfo>>([]));

        _sut = new NotificationInboxRead(
            _inbox,
            new NotificationMeterProvider(
                _users, _google, _teams, _tickets, _applications, _camps, _cache,
                NullLogger<NotificationMeterProvider>.Instance));
    }

    public void Dispose() => _cache.Dispose();

    [HumansFact]
    public async Task Rows_come_from_the_unread_tab_newest_first_across_both_lists()
    {
        var userId = Guid.NewGuid();
        var older = new DateTime(2026, 4, 9, 9, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 4, 10, 9, 0, 0, DateTimeKind.Utc);
        _inbox.GetInboxAsync(userId, null, "all", "unread", Arg.Any<CancellationToken>())
            .Returns(new NotificationInboxResult
            {
                NeedsAttention =
                [
                    new NotificationRowDto
                    {
                        Title = "Consent review pending: Alice",
                        ActionUrl = "/OnboardingReview",
                        Priority = NotificationPriority.High,
                        Source = NotificationSource.ConsentReviewNeeded,
                        Class = NotificationClass.Actionable,
                        CreatedAt = older,
                    },
                ],
                Informational =
                [
                    new NotificationRowDto
                    {
                        Title = "Welcome to Ice Crew",
                        Priority = NotificationPriority.Normal,
                        Source = NotificationSource.TeamMemberAdded,
                        Class = NotificationClass.Informational,
                        CreatedAt = newer,
                        IsRead = true,
                    },
                ],
            });

        var snapshot = await _sut.GetUnreadInboxAsync(Principal(userId), Xunit.TestContext.Current.CancellationToken);

        snapshot.Notifications.Should().SatisfyRespectively(
            first =>
            {
                first.CreatedAt.Should().Be(newer);
                first.Source.Should().Be(NotificationSource.TeamMemberAdded);
                first.ActionUrl.Should().BeNull();
                first.Class.Should().Be(NotificationClass.Informational);
                first.IsUnread.Should().BeFalse();
            },
            second =>
            {
                second.CreatedAt.Should().Be(older);
                second.Title.Should().Be("Consent review pending: Alice");
                second.ActionUrl.Should().Be("/OnboardingReview");
                second.Priority.Should().Be(NotificationPriority.High);
                second.IsUnread.Should().BeTrue();
            });
        snapshot.Meters.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Meters_are_the_principals_role_gated_ones_highest_priority_first()
    {
        var userId = Guid.NewGuid();
        _inbox.GetInboxAsync(userId, null, "all", "unread", Arg.Any<CancellationToken>())
            .Returns(new NotificationInboxResult());
        _google.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(2);
        _tickets.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(true);

        var snapshot = await _sut.GetUnreadInboxAsync(
            Principal(userId, RoleNames.Admin), Xunit.TestContext.Current.CancellationToken);

        snapshot.Meters.Should().Equal(
            new NotificationMeterDto("Failed Google sync events", 2, "/Google/SyncOutbox"),
            new NotificationMeterDto("Ticket sync error", 1, "/Tickets"));
    }

    [HumansFact]
    public async Task A_principal_without_a_user_id_gets_no_rows_and_no_inbox_query()
    {
        var snapshot = await _sut.GetUnreadInboxAsync(new ClaimsPrincipal(new ClaimsIdentity()), Xunit.TestContext.Current.CancellationToken);

        snapshot.Notifications.Should().BeEmpty();
        await _inbox.DidNotReceiveWithAnyArgs().GetInboxAsync(default, default, default!, default!, default);
    }

    private static ClaimsPrincipal Principal(Guid userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }
}
