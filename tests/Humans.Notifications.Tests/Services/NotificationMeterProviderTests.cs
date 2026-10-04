using Humans.Base.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Humans.Notifications.Controllers;
using Humans.Notifications.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Humans.GoogleIntegration.Contracts;
using Humans.Tickets.Contracts;
using Humans.Base.Enums;
using Humans.Notifications.Services;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Camps.Contracts;
using Humans.Governance.Contracts;
using Humans.Teams.Contracts;
using Humans.Base.Caching;
using Humans.Base.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Humans.Users.Contracts;
using Xunit;

namespace Humans.Notifications.Tests.Services;

public class NotificationMeterProviderTests : IDisposable
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IGoogleSyncServiceRead _googleSyncService = Substitute.For<IGoogleSyncServiceRead>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly ITicketSync _ticketSyncService = Substitute.For<ITicketSync>();
    private readonly IApplicationServiceRead _applicationDecisionService = Substitute.For<IApplicationServiceRead>();
    private readonly ICampServiceRead _campService = Substitute.For<ICampServiceRead>();
    private readonly ServiceProvider _localization = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
    private readonly IMemoryCache _cache;
    private readonly NotificationMeterProvider _provider;

    public NotificationMeterProviderTests()
    {
        _cache = new MemoryCache(new MemoryCacheOptions());
        _provider = new NotificationMeterProvider(
            _userService,
            _googleSyncService,
            _teamService,
            _ticketSyncService,
            _applicationDecisionService,
            _campService,
            _cache,
            NullLogger<NotificationMeterProvider>.Instance,
            _localization.GetRequiredService<IStringLocalizer<NotificationsResource>>());
    }

    public void Dispose()
    {
        _cache.Dispose();
        _localization.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetMetersForUserAsync_PropagatesCancellationWithoutCachingZeroCounts(bool campLeadCount)
    {
        var userId = Guid.NewGuid();
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>([]));
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(new Dictionary<Guid, TeamInfo>()));
        _googleSyncService.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(3);
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new CampSettingsInfo(2026, [2026]));
        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([MakeCampInfoWithPendingRequest(userId)]);
        if (campLeadCount)
            _campService.GetCampsForYearAsync(2026, aborted.Token)
                .Returns(Task.FromCanceled<IReadOnlyList<CampInfo>>(aborted.Token));
        else
            _googleSyncService.GetFailedSyncEventCountAsync(aborted.Token)
                .Returns(Task.FromCanceled<int>(aborted.Token));
        var principal = CreatePrincipalWithId(userId, RoleNames.Admin);

        var act = () => _provider.GetMetersForUserAsync(principal, aborted.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        var meters = await _provider.GetMetersForUserAsync(principal, TestContext.Current.CancellationToken);
        if (campLeadCount)
            meters.Should().Contain(m => m.ActionUrl == "/Barrios" && m.Count == 1);
        else
            meters.Should().Contain(m => m.Title == "Failed Google sync events" && m.Count == 3);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationPage_AbortedAfterInboxRead_CancelsMetersWithoutCachingEmptyCounts(bool popup)
    {
        using var aborted = new CancellationTokenSource();
        var abortAfterInbox = true;
        var inbox = Substitute.For<INotificationInboxService>();
        inbox.GetInboxAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (abortAfterInbox) aborted.Cancel();
                return new NotificationInboxResult();
            });
        inbox.GetPopupAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (abortAfterInbox) aborted.Cancel();
                return new NotificationPopupResult();
            });
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>([]));
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(new Dictionary<Guid, TeamInfo>()));
        _googleSyncService.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return 3;
            });
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new CampSettingsInfo(2026, [2026]));
        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns(Array.Empty<CampInfo>());
        var controller = new NotificationsController(inbox, _userService, _provider)
        {
            ControllerContext = new()
            {
                HttpContext = new DefaultHttpContext
                {
                    User = CreatePrincipalWithId(Guid.NewGuid(), RoleNames.Admin),
                    RequestAborted = aborted.Token
                }
            }
        };

        Func<Task<IActionResult>> read = popup ? controller.GetPopup : () => controller.Index(null);
        await read.Should().ThrowAsync<OperationCanceledException>();

        abortAfterInbox = false;
        controller.HttpContext.RequestAborted = TestContext.Current.CancellationToken;
        var result = await read();
        var meters = popup
            ? result.Should().BeOfType<PartialViewResult>().Subject.Model.Should().BeOfType<NotificationPopupViewModel>().Subject.Meters
            : result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<NotificationInboxViewModel>().Subject.Meters;
        meters.Should().Contain(m => m.Title == "Failed Google sync events" && m.Count == 3);
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public async Task MeterTitles_FollowRequestCultureIncludingCampRequestCounts(string culture)
    {
        using var initialCulture = new CultureScope("en");
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<NotificationsResource>>();
        var userId = Guid.NewGuid();
        var users = MakeNeedsConsentReview(1).Append(new User
        {
            Id = Guid.NewGuid(),
            DeletionRequestedAt = Instant.FromUtc(2026, 4, 1, 0, 0)
        }.ToUserInfo()).ToList();
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>(users));
        _googleSyncService.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(3);
        var teamId = Guid.NewGuid();
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(new Dictionary<Guid, TeamInfo>
            {
                [teamId] = new TeamInfo(teamId, "T", null, "t", true, false, SystemTeamType.None,
                    true, false, false, false, Instant.FromUtc(2026, 1, 1, 0, 0), [], PendingRequestCount: 1)
            }));
        _ticketSyncService.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(true);
        _applicationDecisionService.GetUnvotedApplicationCountAsync(userId, Arg.Any<CancellationToken>()).Returns(1);
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new CampSettingsInfo(2026, [2026]));
        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([MakeCampInfoWithPendingRequest(userId)]);
        var principal = CreatePrincipalWithId(userId, RoleNames.Admin, RoleNames.Board, RoleNames.ConsentCoordinator);
        var keys = new[]
        {
            "ConsentReviewsPending", "ApplicationsPendingVote", "PendingAccountDeletions",
            "FailedGoogleSyncEvents", "OnboardingProfilesPending", "TeamJoinRequestsPending",
            "TicketSyncError", "CampJoinRequest"
        };

        _ = await _provider.GetMetersForUserAsync(principal, TestContext.Current.CancellationToken);
        using var requestCulture = new CultureScope(culture);
        var meters = await _provider.GetMetersForUserAsync(principal, TestContext.Current.CancellationToken);

        var expected = keys.Select(key => localizer["Notifications_Meter_" + key]).ToList();
        expected.Should().OnlyContain(value => !value.ResourceNotFound);
        meters.Select(m => m.Title).Should().Equal(expected.Select(value => value.Value));
        _cache.Remove(CacheKeys.CampLeadJoinRequestsBadge(userId));
        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([MakeCampInfoWithPendingRequest(userId), MakeCampInfoWithPendingRequest(userId)]);
        meters = await _provider.GetMetersForUserAsync(principal, TestContext.Current.CancellationToken);
        var plural = localizer["Notifications_Meter_CampJoinRequests", 2];
        plural.ResourceNotFound.Should().BeFalse();
        meters.Should().Contain(m => m.Count == 2 && m.Title == plural.Value);
    }

    [HumansFact]
    public async Task GetMetersForUserAsync_Board_SeesOnboardingMeterMatchingNeedsConsentReview()
    {
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeNeedsConsentReview(1)));
        _googleSyncService.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _ticketSyncService.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(false);
        _applicationDecisionService.GetUnvotedApplicationCountAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(0);

        var meters = await _provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Board), Xunit.TestContext.Current.CancellationToken);

        var onboardingMeter = meters.Single(m =>
            string.Equals(m.Title, "Onboarding profiles pending", StringComparison.Ordinal));
        onboardingMeter.Count.Should().Be(1);
    }

    [HumansFact]
    public async Task GetMetersForUserAsync_VolunteerCoordinator_SeesOnboardingMeter()
    {
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeNeedsConsentReview(1)));
        _googleSyncService.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _ticketSyncService.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(false);

        var meters = await _provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.VolunteerCoordinator), Xunit.TestContext.Current.CancellationToken);

        meters.Should().ContainSingle(m =>
            string.Equals(m.Title, "Onboarding profiles pending", StringComparison.Ordinal) &&
            string.Equals(m.ActionUrl, "/OnboardingReview", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task GetMetersForUserAsync_ConsentCoordinator_SeesConsentReviewsPending()
    {
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeNeedsConsentReview(3)));
        _googleSyncService.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _ticketSyncService.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(false);

        var meters = await _provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.ConsentCoordinator), Xunit.TestContext.Current.CancellationToken);

        meters.Should().ContainSingle(m =>
            string.Equals(m.Title, "Consent reviews pending", StringComparison.Ordinal) &&
            m.Count == 3);
    }

    [HumansFact]
    public async Task GetMetersForUserAsync_Admin_SeesFailedSyncAndDeletionsAndTeamsAndTicketError()
    {
        var usersForDeletion = new[]
        {
            new User { Id = Guid.NewGuid(), DeletionRequestedAt = Instant.FromUtc(2026, 4, 1, 0, 0) },
            new User { Id = Guid.NewGuid(), DeletionRequestedAt = Instant.FromUtc(2026, 4, 2, 0, 0) },
        };
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>(usersForDeletion.Select(u => u.ToUserInfo()).ToList()));
        _googleSyncService.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(5);
        var pendingTeamId = Guid.NewGuid();
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(new Dictionary<Guid, TeamInfo>
            {
                [pendingTeamId] = new TeamInfo(
                    Id: pendingTeamId, Name: "T", Description: null, Slug: "t",
                    IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
                    RequiresApproval: true, IsPublicPage: false, IsHidden: false,
                    IsPromotedToDirectory: false, CreatedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
                    Members: [], PendingRequestCount: 7),
            }));
        _ticketSyncService.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(true);

        var meters = await _provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Admin), Xunit.TestContext.Current.CancellationToken);

        meters.Should().Contain(m => m.Title == "Pending account deletions" && m.Count == 2);
        meters.Should().Contain(m => m.Title == "Failed Google sync events" && m.Count == 5);
        meters.Should().Contain(m => m.Title == "Team join requests pending" && m.Count == 7);
        meters.Should().Contain(m => m.Title == "Ticket sync error" && m.Count == 1);
    }

    [HumansFact]
    public async Task GetMetersForUserAsync_Board_PendingVoteMeter_UsesPerUserCount()
    {
        var boardUserId = Guid.NewGuid();

        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>([]));
        _googleSyncService.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _ticketSyncService.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(false);
        _applicationDecisionService.GetUnvotedApplicationCountAsync(
            boardUserId, Arg.Any<CancellationToken>()).Returns(4);

        var principal = CreatePrincipalWithId(boardUserId, RoleNames.Board);
        var meters = await _provider.GetMetersForUserAsync(principal, Xunit.TestContext.Current.CancellationToken);

        meters.Should().ContainSingle(m =>
            m.Title == "Applications pending your vote" && m.Count == 4);
    }

    [HumansFact]
    public async Task GetMetersForUserAsync_CampLead_SeesPendingRequestsFromCampInfo()
    {
        var leadUserId = Guid.NewGuid();
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>([]));
        _googleSyncService.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(new Dictionary<Guid, TeamInfo>()));
        _ticketSyncService.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(false);
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(2026, [2026]));
        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([MakeCampInfoWithPendingRequest(leadUserId)]);

        var meters = await _provider.GetMetersForUserAsync(CreatePrincipalWithId(leadUserId), Xunit.TestContext.Current.CancellationToken);

        meters.Should().ContainSingle(m =>
            m.Title == "1 human wants to join your camp" &&
            m.Count == 1 &&
            m.ActionUrl == "/Barrios");
    }

    [HumansFact]
    public async Task GetMetersForUserAsync_CampLead_SeesPendingRequestsFromAllOpenCampInfoSeasons()
    {
        var leadUserId = Guid.NewGuid();
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>([]));
        _googleSyncService.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(new Dictionary<Guid, TeamInfo>()));
        _ticketSyncService.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(false);
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(2026, [2027]));
        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([MakeCampInfoWithPendingRequest(Guid.NewGuid())]);
        _campService.GetCampsForYearAsync(2027, Arg.Any<CancellationToken>())
            .Returns([MakeCampInfoWithPendingRequest(leadUserId, 2027)]);

        var meters = await _provider.GetMetersForUserAsync(CreatePrincipalWithId(leadUserId), Xunit.TestContext.Current.CancellationToken);

        meters.Should().ContainSingle(m =>
            m.Title == "1 human wants to join your camp" &&
            m.Count == 1 &&
            m.ActionUrl == "/Barrios");
        await _campService.Received(1).GetCampsForYearAsync(2026, Arg.Any<CancellationToken>());
        await _campService.Received(1).GetCampsForYearAsync(2027, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetMetersForUserAsync_CancelledDuringCounts_ThrowsAndCachesNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyCollection<UserInfo>>>(_ => throw new OperationCanceledException(cts.Token));

        var act = () => _provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Admin), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _cache.TryGetValue(CacheKeys.NotificationMeters, out _).Should().BeFalse();
    }

    [HumansFact]
    public async Task GetMetersForUserAsync_CancelledDuringCampLeadCount_ThrowsAndCachesNothing()
    {
        var leadUserId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>([]));
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<CampSettingsInfo>>(async _ =>
            {
                await cts.CancelAsync();
                throw new OperationCanceledException(cts.Token);
            });

        var act = () => _provider.GetMetersForUserAsync(CreatePrincipalWithId(leadUserId), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _cache.TryGetValue(CacheKeys.CampLeadJoinRequestsBadge(leadUserId), out _).Should().BeFalse();
    }

    private static ClaimsPrincipal CreatePrincipal(params string[] roles)
    {
        var claims = roles.Select(role => new Claim(ClaimTypes.Role, role));
        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal CreatePrincipalWithId(Guid userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    private static IReadOnlyCollection<UserInfo> MakeNeedsConsentReview(int count) =>
        Enumerable.Range(0, count).Select(_ =>
        {
            var userId = Guid.NewGuid();
            return UserInfo.Create(
                user: new User
                {
                    Id = userId,
                    DisplayName = "U",
                    PreferredLanguage = "en",
                    CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                },
                userEmails: [],
                eventParticipations: [],
                externalLogins: [],
                profile: UserFixtures.Profile(
                    burnerName: "B",
                    firstName: "F",
                    lastName: "L",
                    isApproved: false),
                communicationPreferences: []);
        }).ToList();

    private static CampInfo MakeCampInfoWithPendingRequest(Guid leadUserId, int year = 2026)
    {
        var campId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var pendingMember = new CampSeasonMemberInfo(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CampMemberStatus.Pending,
            Instant.FromUtc(2026, 3, 1, 0, 0),
            ConfirmedAt: null,
            HasEarlyEntry: false);

        return new CampInfo(
            campId,
            "lead-camp",
            "lead@example.com",
            "+34600000000",
            IsSwissCamp: false,
            TimesAtNowhere: 1,
            Seasons:
            [
                new CampSeasonInfo(
                    seasonId, campId, "lead-camp", year, null, "Lead Camp",
                    string.Empty, string.Empty, [], CampSeasonStatus.Active,
                    YesNoMaybe.No, YesNoMaybe.No, AdultPlayspacePolicy.No,
                    0, null, null, null, 0, null, null)
                {
                    LeadUserIds = [leadUserId],
                    Members = [pendingMember]
                }
            ]);
    }
}
