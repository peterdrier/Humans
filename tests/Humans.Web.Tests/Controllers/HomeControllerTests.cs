using System.Collections.Generic;
using System.Security.Claims;
using Humans.Application.Configuration;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Dashboard;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Testing;
using Humans.Web.Authorization;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Verifies that <see cref="HomeController.Index"/> defers to
/// <see cref="IOnboardingWidgetState"/> when an authenticated user lands on /,
/// redirecting incomplete users into the onboarding widget and only falling
/// through to the dashboard branch once every required step is finished.
/// </summary>
public class HomeControllerTests
{
    private readonly UserManager<User> _userManager;
    private readonly IDashboardService _dashboardService = Substitute.For<IDashboardService>();
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IOnboardingWidgetState _widgetState = Substitute.For<IOnboardingWidgetState>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly ConfigurationRegistry _configRegistry = new();

    public HomeControllerTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
    }

    private HomeController BuildSut(User user, bool hasProfile = true)
    {
        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(user);

        var ctrl = new HomeController(
            _userManager,
            _dashboardService,
            _shiftMgmt,
            _userService,
            _widgetState,
            _configuration,
            _configRegistry,
            NullLogger<HomeController>.Instance);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        };
        if (hasProfile)
        {
            claims.Add(new Claim(
                RoleAssignmentClaimsTransformation.HasProfileClaimType,
                RoleAssignmentClaimsTransformation.ActiveClaimValue));
        }

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
        };
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }

    [HumansFact]
    public async Task Index_RedirectsToOnboardingWidget_WhenStepNotComplete()
    {
        var user = new User { Id = Guid.NewGuid(), DisplayName = "Test" };
        _widgetState.GetCurrentStepAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(OnboardingWidgetStep.Names);
        var ctrl = BuildSut(user);

        var result = await ctrl.Index(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("OnboardingWidget", redirect.ControllerName);
    }

    [HumansFact]
    public async Task Index_RendersDashboard_WhenStepComplete()
    {
        var user = new User { Id = Guid.NewGuid(), DisplayName = "Test" };
        _widgetState.GetCurrentStepAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(OnboardingWidgetStep.Complete);
        _dashboardService
            .GetMemberDashboardAsync(user.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(BuildEmptyDashboard());
        var ctrl = BuildSut(user);

        var result = await ctrl.Index(CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Dashboard", view.ViewName);
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
                MissingConsentVersionIds: Array.Empty<Guid>()),
            LatestApplication: null,
            HasPendingApplication: false,
            CurrentTier: MembershipTier.Volunteer,
            TermExpiresAt: null,
            TermExpiresSoon: false,
            TermExpired: false,
            ActiveEvent: null,
            UrgentShifts: Array.Empty<DashboardUrgentShift>(),
            NextShifts: Array.Empty<DashboardSignup>(),
            PendingSignupCount: 0,
            HasShiftSignups: false,
            TicketsConfigured: false,
            HasTicket: false,
            UserTicketCount: 0,
            ParticipationStatus: null);
    }
}
