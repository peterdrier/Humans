using System.Security.Claims;
using AwesomeAssertions;
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
using Humans.Users.Contracts;

namespace Humans.Shifts.Tests.Controllers;

/// <summary>
/// The knobs form moved to /Settings#shifts (peterdrier/Humans#1634). The old
/// <c>GET /Shifts/Settings</c> page is retired outright, with no redirect kept, since
/// redirecting a legacy URL is tech debt here. <c>POST /Shifts/Settings</c> stays (the
/// tab posts to it) and now redirects back to the tab (post-redirect-get, not a
/// legacy-URL redirect).
/// </summary>
public sealed class ShiftsControllerSettingsTests
{
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly IShiftSignupService _signupService = Substitute.For<IShiftSignupService>();
    private readonly IVolunteerTrackingService _volunteerTrackingService = Substitute.For<IVolunteerTrackingService>();
    private readonly IShiftRowView _shiftView = Substitute.For<IShiftRowView>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IStringLocalizer<ShiftsResource> _localizer = Substitute.For<IStringLocalizer<ShiftsResource>>();
    private readonly IStringLocalizer<OnboardingResource> _onboardingLocalizer = Substitute.For<IStringLocalizer<OnboardingResource>>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ShiftBrowsePageBuilder _builder;
    private readonly ILogger<ShiftsController> _logger = NullLogger<ShiftsController>.Instance;

    private static readonly BurnSettingsInfo ActiveEvent = new(
        Id: Guid.NewGuid(),
        EventName: "Test Event",
        Year: 2026,
        TimeZoneId: "UTC",
        GateOpeningDate: new LocalDate(2026, 7, 1),
        BuildStartOffset: -14,
        EventEndOffset: 6,
        StrikeEndOffset: 9,
        FirstCrewStartOffset: -14,
        SetupWeekStartOffset: -10,
        PreEventWeekStartOffset: -7,
        FinishingWeekendStartOffset: -3,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null,
        IsShiftBrowsingOpen: true);

    public ShiftsControllerSettingsTests()
    {
        _localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        _builder = new ShiftBrowsePageBuilder(_shiftMgmt, _burnSettings, _teamService);
    }

    private ShiftsController BuildSut()
    {
        var ctrl = new ShiftsController(
            _shiftMgmt, _burnSettings, _signupService, _volunteerTrackingService, _shiftView, _teamService,
            _auditLogService, _userService, _localizer, _onboardingLocalizer, _clock, _builder, _logger);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test")),
        };
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        http.RequestServices = services.BuildServiceProvider();
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return ctrl;
    }

    [HumansFact]
    public async Task Settings_Post_ValidModel_SavesKnobsAndRedirectsToTheSettingsPageShiftsTab()
    {
        _burnSettings.GetActiveAsync().Returns(ActiveEvent);
        var ctrl = BuildSut();
        var model = new EventSettingsViewModel
        {
            IsShiftBrowsingOpen = true,
            GlobalVolunteerCap = 50,
            ReminderLeadTimeHours = 12,
        };

        var result = await ctrl.Settings(model);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#shifts");
        await _shiftMgmt.Received(1).SaveKnobsAsync(ActiveEvent.Id, true, 50, 12);
    }

    [HumansFact]
    public async Task Settings_Post_NoActiveEvent_RedirectsWithoutSaving()
    {
        _burnSettings.GetActiveAsync().Returns((BurnSettingsInfo?)null);
        var ctrl = BuildSut();
        var model = new EventSettingsViewModel();

        var result = await ctrl.Settings(model);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#shifts");
        await _shiftMgmt.DidNotReceive().SaveKnobsAsync(
            Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<int>());
    }

    [HumansFact]
    public async Task Settings_Post_InvalidModel_RedirectsWithoutSaving()
    {
        var ctrl = BuildSut();
        ctrl.ModelState.AddModelError("ReminderLeadTimeHours", "Invalid.");
        var model = new EventSettingsViewModel();

        var result = await ctrl.Settings(model);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#shifts");
        await _shiftMgmt.DidNotReceive().SaveKnobsAsync(
            Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<int>());
    }
}
