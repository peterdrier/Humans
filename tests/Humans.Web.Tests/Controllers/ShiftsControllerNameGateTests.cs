using System.Security.Claims;
using Humans.Application;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Humans.Web.Models.Shifts;
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

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Stub-name gate on <see cref="ShiftsController"/> — users with no legal name
/// on file cannot browse or sign up for shifts. The check sits in front of every
/// browse/signup entry so deep links to /Shifts (bypassing the OnboardingWidget
/// dispatcher) still get bounced to the onboarding flow.
/// </summary>
public class ShiftsControllerNameGateTests
{
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IShiftSignupService _signupService = Substitute.For<IShiftSignupService>();
    private readonly IVolunteerTrackingService _volunteerTrackingService = Substitute.For<IVolunteerTrackingService>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IStringLocalizer<SharedResource> _localizer = Substitute.For<IStringLocalizer<SharedResource>>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ShiftBrowsePageBuilder _builder;
    private readonly ILogger<ShiftsController> _logger = NullLogger<ShiftsController>.Instance;

    public ShiftsControllerNameGateTests()
    {
        _localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        _builder = new ShiftBrowsePageBuilder(_shiftMgmt, _teamService);
    }

    private ShiftsController BuildSut(Guid userId, UserInfo userInfo)
    {
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(userInfo);
        var ctrl = new ShiftsController(
            _shiftMgmt, _signupService, _volunteerTrackingService, _shiftView, _teamService,
            _auditLogService, _userService, _localizer, _clock, _builder, _logger);
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
        UserInfo.Create(
            user: new User
            {
                Id = userId,
                DisplayName = burner,
                PreferredLanguage = "en",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                GoogleEmailStatus = GoogleEmailStatus.Unknown,
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
                State = string.IsNullOrWhiteSpace(burner) || string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last)
                    ? ProfileState.Stub
                    : ProfileState.Active,
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
        Assert.Equal(nameof(OnboardingWidgetController.Index), redirect.ActionName);
        Assert.Equal("OnboardingWidget", redirect.ControllerName);
        await _shiftMgmt.DidNotReceiveWithAnyArgs().GetActiveAsync();
    }

    [HumansFact]
    public async Task SignUp_NamelessUser_RedirectsToOnboardingWidget_WithoutSigningUp()
    {
        var userId = Guid.NewGuid();
        var ctrl = BuildSut(userId, MakeUserInfo(userId, burner: "", first: "", last: ""));

        var result = await ctrl.SignUp(
            shiftId: Guid.NewGuid(), departmentId: null, fromDate: null, toDate: null,
            period: null, tagIds: null);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Index), redirect.ActionName);
        Assert.Equal("OnboardingWidget", redirect.ControllerName);
        await _signupService.DidNotReceiveWithAnyArgs().SignUpAsync(Guid.Empty, Guid.Empty);
    }

    [HumansFact]
    public async Task SignUpRange_NamelessUser_RedirectsToOnboardingWidget_WithoutSigningUp()
    {
        var userId = Guid.NewGuid();
        var ctrl = BuildSut(userId, MakeUserInfo(userId, burner: "", first: "", last: ""));

        var result = await ctrl.SignUpRange(
            rotaId: Guid.NewGuid(), startDayOffset: 0, endDayOffset: 1,
            departmentId: null, fromDate: null, toDate: null, period: null, tagIds: null);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Index), redirect.ActionName);
        Assert.Equal("OnboardingWidget", redirect.ControllerName);
        await _signupService.DidNotReceiveWithAnyArgs().SignUpRangeAsync(
            Guid.Empty, Guid.Empty, 0, 0);
    }

    [HumansFact]
    public async Task Index_NamedUser_PassesGate()
    {
        // Confirms the gate doesn't break the happy path. With no active event,
        // the controller short-circuits to a "NoActiveEvent" view — proving the
        // gate let the request through.
        var userId = Guid.NewGuid();
        var ctrl = BuildSut(userId, MakeUserInfo(userId, burner: "B", first: "F", last: "L"));
        _shiftMgmt.GetActiveAsync().Returns((EventSettings?)null);

        var result = await ctrl.Index(
            departmentId: null, fromDate: null, toDate: null, period: null);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("NoActiveEvent", view.ViewName);
    }
}
