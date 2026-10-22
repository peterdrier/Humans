using System.Security.Claims;
using Humans.Onboarding;
using Humans.AuditLog.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Shifts.Controllers;
using Humans.Shifts.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;
using Humans.Users.Contracts;
using Humans.Users.Domain;
using Humans.Users.Services;

namespace Humans.Shifts.Tests.Controllers;

/// <summary>
/// The gate sits on the browse entry so deep links to /Shifts that bypass the
/// onboarding widget still bounce to onboarding.
/// </summary>
public class ShiftsControllerNameGateTests
{
    private readonly IShiftManagement _shiftMgmt = Substitute.For<IShiftManagement>();
    private readonly IShiftSignupService _signupService = Substitute.For<IShiftSignupService>();
    private readonly IVolunteerTrackingManagement _volunteerTrackingService = Substitute.For<IVolunteerTrackingManagement>();
    private readonly IShiftRowView _shiftView = Substitute.For<IShiftRowView>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IStringLocalizer<ShiftsResource> _localizer = Substitute.For<IStringLocalizer<ShiftsResource>>();
    private readonly IStringLocalizer<OnboardingResource> _onboardingLocalizer = Substitute.For<IStringLocalizer<OnboardingResource>>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly ShiftBrowsePageBuilder _builder;
    private readonly ILogger<ShiftsController> _logger = NullLogger<ShiftsController>.Instance;

    public ShiftsControllerNameGateTests()
    {
        _localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        _onboardingLocalizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        _builder = new ShiftBrowsePageBuilder(_shiftMgmt, _burnSettings, _teamService);
    }

    private ShiftsController BuildSut(Guid userId, UserInfo userInfo)
    {
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(userInfo);
        var ctrl = new ShiftsController(
            _shiftMgmt, _burnSettings, _signupService, _volunteerTrackingService, _shiftView, _teamService,
            _auditLogService, _userService, _localizer, _onboardingLocalizer, _clock, _builder, _logger);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
        };
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        http.RequestServices = services.BuildServiceProvider();
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        // Pre-set Url so RedirectToAction doesn't resolve IUrlHelperFactory from RequestServices.
        ctrl.Url = Substitute.For<IUrlHelper>();
        return ctrl;
    }

    private static UserInfo MakeUserInfo(Guid userId, string burner, string first, string last) =>
        UserInfoFactory.Create(
            user: new User
            {
                Id = userId,
                DisplayName = burner,
                PreferredLanguage = "en",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                State = string.IsNullOrWhiteSpace(burner) || string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last)
                    ? UserState.Bare
                    : UserState.Active,
            },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: new Profile
            {
                UserId = userId,
                BurnerName = burner,
                FirstName = first,
                LastName = last,
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            },
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);

    [HumansFact]
    public async Task Index_NamelessUser_RedirectsToOnboardingWidget()
    {
        var userId = Guid.NewGuid();
        var ctrl = BuildSut(userId, MakeUserInfo(userId, burner: "", first: "", last: ""));

        var result = await ctrl.Index(
            departmentId: null, fromDate: null, toDate: null, period: null);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("OnboardingWidget", redirect.ControllerName);
        await _burnSettings.DidNotReceiveWithAnyArgs().GetActiveAsync();
    }

    [HumansFact]
    public async Task Index_NamedUser_PassesGate()
    {
        // With no active event the controller short-circuits to "NoActiveEvent",
        // which proves the gate let the request through.
        var userId = Guid.NewGuid();
        var ctrl = BuildSut(userId, MakeUserInfo(userId, burner: "B", first: "F", last: "L"));
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns((BurnSettingsInfo?)null);

        var result = await ctrl.Index(
            departmentId: null, fromDate: null, toDate: null, period: null);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("NoActiveEvent", view.ViewName);
    }
}
