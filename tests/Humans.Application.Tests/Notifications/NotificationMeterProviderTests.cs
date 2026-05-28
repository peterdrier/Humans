using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using NotificationMeterProvider = Humans.Application.Services.Notifications.NotificationMeterProvider;

namespace Humans.Application.Tests.Notifications;

public class NotificationMeterProviderTests : IDisposable
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IGoogleSyncService _googleSyncService = Substitute.For<IGoogleSyncService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly ITicketSyncService _ticketSyncService = Substitute.For<ITicketSyncService>();
    private readonly IApplicationDecisionService _applicationDecisionService = Substitute.For<IApplicationDecisionService>();
    private readonly ICampServiceRead _campService = Substitute.For<ICampServiceRead>();
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
            NullLogger<NotificationMeterProvider>.Instance);
    }

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task GetMetersForUserAsync_Board_SeesOnboardingMeterMatchingNeedsConsentReview()
    {
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeNeedsConsentReview(1)));
        _userService.GetAllUsersAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<User>)[]);
        _googleSyncService.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _teamService.GetTotalPendingJoinRequestCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _ticketSyncService.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(false);
        _applicationDecisionService.GetUnvotedApplicationCountAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(0);

        var meters = await _provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Board));

        var onboardingMeter = meters.Single(m =>
            string.Equals(m.Title, "Onboarding profiles pending", StringComparison.Ordinal));
        onboardingMeter.Count.Should().Be(1);
    }

    [HumansFact]
    public async Task GetMetersForUserAsync_VolunteerCoordinator_SeesOnboardingMeter()
    {
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeNeedsConsentReview(1)));
        _userService.GetAllUsersAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<User>)[]);
        _googleSyncService.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _teamService.GetTotalPendingJoinRequestCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _ticketSyncService.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(false);

        var meters = await _provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.VolunteerCoordinator));

        meters.Should().ContainSingle(m =>
            string.Equals(m.Title, "Onboarding profiles pending", StringComparison.Ordinal) &&
            string.Equals(m.ActionUrl, "/OnboardingReview", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task GetMetersForUserAsync_ConsentCoordinator_SeesConsentReviewsPending()
    {
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeNeedsConsentReview(3)));
        _userService.GetAllUsersAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<User>)[]);
        _googleSyncService.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _teamService.GetTotalPendingJoinRequestCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _ticketSyncService.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(false);

        var meters = await _provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.ConsentCoordinator));

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
        _userService.GetAllUsersAsync(Arg.Any<CancellationToken>())
            .Returns(usersForDeletion);
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

        var meters = await _provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Admin));

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
        _userService.GetAllUsersAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<User>)[]);
        _googleSyncService.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _teamService.GetTotalPendingJoinRequestCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _ticketSyncService.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(false);
        _applicationDecisionService.GetUnvotedApplicationCountAsync(
            boardUserId, Arg.Any<CancellationToken>()).Returns(4);

        var principal = CreatePrincipalWithId(boardUserId, RoleNames.Board);
        var meters = await _provider.GetMetersForUserAsync(principal);

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
            .Returns(new CampSettingsInfo(2026, [2026], null));
        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([MakeCampInfoWithPendingRequest(leadUserId)]);

        var meters = await _provider.GetMetersForUserAsync(CreatePrincipalWithId(leadUserId));

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
            .Returns(new CampSettingsInfo(2026, [2027], null));
        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([MakeCampInfoWithPendingRequest(Guid.NewGuid(), 2026)]);
        _campService.GetCampsForYearAsync(2027, Arg.Any<CancellationToken>())
            .Returns([MakeCampInfoWithPendingRequest(leadUserId, 2027)]);

        var meters = await _provider.GetMetersForUserAsync(CreatePrincipalWithId(leadUserId));

        meters.Should().ContainSingle(m =>
            m.Title == "1 human wants to join your camp" &&
            m.Count == 1 &&
            m.ActionUrl == "/Barrios");
        await _campService.Received(1).GetCampsForYearAsync(2026, Arg.Any<CancellationToken>());
        await _campService.Received(1).GetCampsForYearAsync(2027, Arg.Any<CancellationToken>());
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
                    GoogleEmailStatus = GoogleEmailStatus.Unknown,
                },
                userEmails: [],
                eventParticipations: [],
                externalLogins: [],
                profile: new Profile
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    BurnerName = "B",
                    FirstName = "F",
                    LastName = "L",
                    State = ProfileState.Active,
                    IsApproved = false,
                    RejectedAt = null,
                    CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                    UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                },
                contactFields: [],
                profileLanguages: [],
                volunteerHistory: [],
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
