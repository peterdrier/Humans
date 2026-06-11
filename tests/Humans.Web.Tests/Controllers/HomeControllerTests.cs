using System.Security.Claims;
using Humans.Application;
using Humans.Application.Configuration;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Dashboard;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Verifies <see cref="HomeController.Index"/> after the name-only access switch. The
/// <see cref="Humans.Web.Authorization.MembershipRequiredFilter"/> now routes every non-Active user
/// away before the action runs, so the controller no longer gates on onboarding completion: it
/// renders the dashboard for any authenticated, resolvable user and the public landing view for
/// anonymous visitors.
/// </summary>
public class HomeControllerTests
{
    private readonly IDashboardService _dashboardService = Substitute.For<IDashboardService>();
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly ConfigurationRegistry _configRegistry = new();

    private HomeController BuildSut(ClaimsPrincipal principal)
    {
        var ctrl = new HomeController(
            _userService,
            _dashboardService,
            _shiftMgmt,
            _configuration,
            _configRegistry,
            NullLogger<HomeController>.Instance);

        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal },
        };
        return ctrl;
    }

    private static ClaimsPrincipal Authenticated(Guid userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));

    private void StubUserInfo(Guid userId)
    {
        var user = new User { Id = userId, DisplayName = "Test" };
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(
                user, [], [], [], profile: null, [], [], [], [])));
    }

    [HumansFact]
    public async Task Index_RendersDashboard_ForAuthenticatedUser()
    {
        var userId = Guid.NewGuid();
        StubUserInfo(userId);
        _dashboardService.GetMemberDashboardAsync(userId, Arg.Any<CancellationToken>())
            .Returns(BuildEmptyDashboard());

        var result = await BuildSut(Authenticated(userId)).Index(TestContext.Current.CancellationToken);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Dashboard", view.ViewName);
    }

    [HumansFact]
    public async Task Index_RendersLandingView_ForAnonymousVisitor()
    {
        var result = await BuildSut(new ClaimsPrincipal(new ClaimsIdentity()))
            .Index(TestContext.Current.CancellationToken);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.ViewName);
    }

    private static MemberDashboardData BuildEmptyDashboard()
    {
        return new MemberDashboardData(
            Profile: null,
            MembershipSnapshot: new MembershipSnapshot(
                Status: MembershipStatus.Active,
                IsVolunteerMember: true,
                RequiredConsentCount: 0,
                PendingConsentCount: 0,
                MissingConsentVersionIds: []),
            LatestApplication: null,
            HasPendingApplication: false,
            CurrentTier: MembershipTier.Volunteer,
            TermExpiresAt: null,
            TermExpiresSoon: false,
            TermExpired: false,
            ActiveEvent: null,
            UrgentShifts: [],
            NextShifts: [],
            PendingSignupCount: 0,
            HasShiftSignups: false,
            TicketsConfigured: false,
            HasTicket: false,
            UserTicketCount: 0,
            ParticipationStatus: null);
    }
}
