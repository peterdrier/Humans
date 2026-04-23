using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Governance;
using Humans.Domain.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using NotificationMeterProvider = Humans.Application.Services.Notifications.NotificationMeterProvider;

namespace Humans.Application.Tests.Services;

public class NotificationMeterProviderTests : IDisposable
{
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IGoogleSyncService _googleSyncService = Substitute.For<IGoogleSyncService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly ITicketSyncService _ticketSyncService = Substitute.For<ITicketSyncService>();
    private readonly IApplicationDecisionService _applicationDecisionService = Substitute.For<IApplicationDecisionService>();
    private readonly IMemoryCache _cache;
    private readonly NotificationMeterProvider _provider;

    public NotificationMeterProviderTests()
    {
        _cache = new MemoryCache(new MemoryCacheOptions());
        _provider = new NotificationMeterProvider(
            _profileService,
            _userService,
            _googleSyncService,
            _teamService,
            _ticketSyncService,
            _applicationDecisionService,
            _cache,
            NullLogger<NotificationMeterProvider>.Instance);
    }

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetMetersForUserAsync_Board_OnboardingMeterExcludesConsentReviewItems()
    {
        // consentReviewsPending = 1, totalNotApproved = 2 → onboardingPending = 1
        _profileService.GetConsentReviewPendingCountAsync(Arg.Any<CancellationToken>()).Returns(1);
        _profileService.GetNotApprovedAndNotSuspendedCountAsync(Arg.Any<CancellationToken>()).Returns(2);
        _userService.GetPendingDeletionCountAsync(Arg.Any<CancellationToken>()).Returns(0);
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

    [Fact]
    public async Task GetMetersForUserAsync_VolunteerCoordinator_SeesOnboardingMeter()
    {
        _profileService.GetConsentReviewPendingCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _profileService.GetNotApprovedAndNotSuspendedCountAsync(Arg.Any<CancellationToken>()).Returns(1);
        _userService.GetPendingDeletionCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _googleSyncService.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _teamService.GetTotalPendingJoinRequestCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _ticketSyncService.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(false);

        var meters = await _provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.VolunteerCoordinator));

        meters.Should().ContainSingle(m =>
            string.Equals(m.Title, "Onboarding profiles pending", StringComparison.Ordinal) &&
            string.Equals(m.ActionUrl, "/OnboardingReview", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetMetersForUserAsync_ConsentCoordinator_SeesConsentReviewsPending()
    {
        _profileService.GetConsentReviewPendingCountAsync(Arg.Any<CancellationToken>()).Returns(3);
        _profileService.GetNotApprovedAndNotSuspendedCountAsync(Arg.Any<CancellationToken>()).Returns(3);
        _userService.GetPendingDeletionCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _googleSyncService.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _teamService.GetTotalPendingJoinRequestCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _ticketSyncService.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(false);

        var meters = await _provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.ConsentCoordinator));

        meters.Should().ContainSingle(m =>
            string.Equals(m.Title, "Consent reviews pending", StringComparison.Ordinal) &&
            m.Count == 3);
    }

    [Fact]
    public async Task GetMetersForUserAsync_Admin_SeesFailedSyncAndDeletionsAndTeamsAndTicketError()
    {
        _profileService.GetConsentReviewPendingCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _profileService.GetNotApprovedAndNotSuspendedCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _userService.GetPendingDeletionCountAsync(Arg.Any<CancellationToken>()).Returns(2);
        _googleSyncService.GetFailedSyncEventCountAsync(Arg.Any<CancellationToken>()).Returns(5);
        _teamService.GetTotalPendingJoinRequestCountAsync(Arg.Any<CancellationToken>()).Returns(7);
        _ticketSyncService.IsInErrorStateAsync(Arg.Any<CancellationToken>()).Returns(true);

        var meters = await _provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Admin));

        meters.Should().Contain(m => m.Title == "Pending account deletions" && m.Count == 2);
        meters.Should().Contain(m => m.Title == "Failed Google sync events" && m.Count == 5);
        meters.Should().Contain(m => m.Title == "Team join requests pending" && m.Count == 7);
        meters.Should().Contain(m => m.Title == "Ticket sync error" && m.Count == 1);
    }

    [Fact]
    public async Task GetMetersForUserAsync_Board_PendingVoteMeter_UsesPerUserCount()
    {
        var boardUserId = Guid.NewGuid();

        _profileService.GetConsentReviewPendingCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _profileService.GetNotApprovedAndNotSuspendedCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        _userService.GetPendingDeletionCountAsync(Arg.Any<CancellationToken>()).Returns(0);
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
}
