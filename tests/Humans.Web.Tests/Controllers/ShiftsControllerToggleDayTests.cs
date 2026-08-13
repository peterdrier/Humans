using System.Security.Claims;
using AwesomeAssertions;
using Humans.Onboarding;
using Humans.Application;
using Humans.AuditLog.Contracts;
using Humans.Application.Interfaces.Shifts;
using Humans.Teams.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.UI;
using Humans.Web.Controllers;
using Humans.Web.Models.Shifts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// <see cref="ShiftsController.ToggleDay"/> — the AJAX per-day signup/bail toggle.
/// The bail-vs-signup decision and the dietary gate now live in
/// <see cref="IShiftSignupService.ToggleDayAsync"/> (see
/// <c>ShiftSignupServiceTests</c> for that coverage); these tests only assert
/// the controller's mapping of a stubbed <see cref="ToggleDaySignupOutcome"/>
/// onto the response metadata headers (X-Signed-Up, X-Toast-*, X-Redirect) and
/// view selection, since that mapping is the contract the client JS depends on.
/// </summary>
public class ShiftsControllerToggleDayTests
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IShiftSignupService _signupService = Substitute.For<IShiftSignupService>();
    private readonly IVolunteerTrackingService _volunteerTrackingService = Substitute.For<IVolunteerTrackingService>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IStringLocalizer<SharedResource> _localizer = Substitute.For<IStringLocalizer<SharedResource>>();
    // The name-gate message came home with Onboarding's G5; the controller now resolves it
    // through the section's own resource set.
    private readonly IStringLocalizer<OnboardingResource> _onboardingLocalizer = Substitute.For<IStringLocalizer<OnboardingResource>>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ShiftBrowsePageBuilder _builder;
    private readonly ILogger<ShiftsController> _logger = NullLogger<ShiftsController>.Instance;

    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();

    private static readonly BurnSettingsInfo Event = new(
        Id: Guid.NewGuid(),
        EventName: "Test Event 2026",
        Year: 2026,
        TimeZoneId: "Europe/Madrid",
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

    public ShiftsControllerToggleDayTests()
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
        // Pre-set Url so RedirectToAction/Url.Action don't resolve IUrlHelperFactory from RequestServices.
        // Url.Action returns null on this substitute (no routing in a unit test) — exercised below.
        ctrl.Url = Substitute.For<IUrlHelper>();
        return ctrl;
    }

    private static UserInfo MakeUserInfo(
        Guid userId, string burner, string first, string last, string? dietary)
    {
        var profile = new Profile
        {
            UserId = userId,
            BurnerName = burner,
            FirstName = first,
            LastName = last,
            DietaryPreference = dietary,
            State = string.IsNullOrWhiteSpace(burner) || string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last)
                ? ProfileState.Stub
                : ProfileState.Active,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };
        return UserInfo.Create(
            user: new User
            {
                Id = userId,
                DisplayName = burner,
                PreferredLanguage = "en",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: profile,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
    }

    // Stub the builder dependencies so BuildRowAsync returns a row for shiftId.
    // Mirrors ShiftBrowsePageBuilderRowTests: an all-day UrgentShift with Shift.Id == shiftId.
    private void StubBrowseRow(Guid shiftId, Guid userId, SignupStatus? rowStatus)
    {
        var shift = new Shift
        {
            Id = shiftId,
            RotaId = Guid.NewGuid(),
            DayOffset = 1,
            IsAllDay = true,
            StartTime = new LocalTime(8, 0),
            Duration = Duration.FromHours(4),
            MinVolunteers = 2,
            MaxVolunteers = 5,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        var signups = rowStatus is { } st
            ? new List<(Guid, string, SignupStatus)> { (userId, "Tester", st) }
            : [];
        var urgent = new UrgentShift(
            shift,
            UrgencyScore: 1.5,
            ConfirmedCount: 3,
            RemainingSlots: 2,
            DepartmentName: "Test Department",
            Signups: signups);

        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(Event);
        _shiftMgmt.GetBrowseShiftsAsync(Arg.Any<ShiftBrowseQuery>())
            .Returns(new List<UrgentShift> { urgent });
    }

    private static ShiftSignup ActiveSignup(Guid id, Guid userId, Guid shiftId, SignupStatus status) =>
        new()
        {
            Id = id,
            UserId = userId,
            ShiftId = shiftId,
            Status = status,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };

    private void StubToggleOutcome(ToggleDaySignupOutcome outcome) =>
        _signupService.ToggleDayAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>()).Returns(outcome);

    [HumansFact]
    public async Task ToggleDay_WhenNotSignedUp_SignsUp_AndSetsSignedUpHeaderTrue()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var ctrl = BuildSut(userId, MakeUserInfo(userId, "B", "F", "L", dietary: "Vegan"));

        var created = ActiveSignup(Guid.NewGuid(), userId, shiftId, SignupStatus.Confirmed);
        StubToggleOutcome(new ToggleDaySignupOutcome(false, SignupResult.Ok(created), true, false, [created]));
        StubBrowseRow(shiftId, userId, SignupStatus.Confirmed);

        var result = await ctrl.ToggleDay(shiftId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<PartialViewResult>();
        ctrl.Response.Headers["X-Signed-Up"].ToString().Should().Be("true");
        await _signupService.Received(1).ToggleDayAsync(
            userId, shiftId, Event.Id, /* privileged */ false, /* hasDietaryPreference */ true,
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ToggleDay_WhenSignedUp_Bails_AndSetsSignedUpHeaderFalse()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var signupId = Guid.NewGuid();
        var ctrl = BuildSut(userId, MakeUserInfo(userId, "B", "F", "L", dietary: "Vegan"));

        var existing = ActiveSignup(signupId, userId, shiftId, SignupStatus.Confirmed);
        StubToggleOutcome(new ToggleDaySignupOutcome(false, SignupResult.Ok(existing), false, false, []));
        StubBrowseRow(shiftId, userId, rowStatus: null);

        var result = await ctrl.ToggleDay(shiftId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<PartialViewResult>();
        ctrl.Response.Headers["X-Signed-Up"].ToString().Should().Be("false");
    }

    [HumansFact]
    public async Task ToggleDay_OnOverlapFail_SetsWarningToast_AndDoesNotSignUp()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var ctrl = BuildSut(userId, MakeUserInfo(userId, "B", "F", "L", dietary: "Vegan"));

        StubToggleOutcome(new ToggleDaySignupOutcome(
            false, SignupResult.Fail("Time conflict on day(s): Mon"), false, false, []));
        StubBrowseRow(shiftId, userId, rowStatus: null);

        var result = await ctrl.ToggleDay(shiftId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<PartialViewResult>();
        ctrl.Response.Headers["X-Toast-Type"].ToString().Should().Be("warning");
        ctrl.Response.Headers["X-Toast-Msg"].ToString().Should().NotBeNullOrEmpty();
        ctrl.Response.Headers["X-Signed-Up"].ToString().Should().Be("false");
    }

    [HumansFact]
    public async Task ToggleDay_SignUpRequiringApproval_SetsAppliedToast()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var ctrl = BuildSut(userId, MakeUserInfo(userId, "B", "F", "L", dietary: "Vegan"));

        var pending = ActiveSignup(Guid.NewGuid(), userId, shiftId, SignupStatus.Pending);
        StubToggleOutcome(new ToggleDaySignupOutcome(false, SignupResult.Ok(pending), true, false, [pending]));
        StubBrowseRow(shiftId, userId, SignupStatus.Pending);

        var result = await ctrl.ToggleDay(shiftId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<PartialViewResult>();
        ctrl.Response.Headers["X-Signed-Up"].ToString().Should().Be("true");
        ctrl.Response.Headers["X-Toast-Type"].ToString().Should().Be("success");
        // localizer echoes keys in tests, so the "Applied" key flows into the toast.
        ctrl.Response.Headers["X-Toast-Msg"].ToString().Should().Contain("Applied");
    }

    [HumansFact]
    public async Task ToggleDay_WhenRowNoLongerVisible_RedirectsToReload()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var ctrl = BuildSut(userId, MakeUserInfo(userId, "B", "F", "L", dietary: "Vegan"));

        var created = ActiveSignup(Guid.NewGuid(), userId, shiftId, SignupStatus.Confirmed);
        StubToggleOutcome(new ToggleDaySignupOutcome(false, SignupResult.Ok(created), true, false, [created]));
        // Active event present, but the toggled shift isn't in the browse set → BuildRowAsync
        // returns null; the controller must resync (204) instead of throwing.
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(Event);
        _shiftMgmt.GetBrowseShiftsAsync(Arg.Any<ShiftBrowseQuery>()).Returns(new List<UrgentShift>());

        var result = await ctrl.ToggleDay(shiftId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeAssignableTo<IStatusCodeActionResult>()
            .Which.StatusCode.Should().Be(204);
    }

    [HumansFact]
    public async Task ToggleDay_WhenDietaryMissing_Returns204_WithRedirectHeader()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        // Named (passes name gate) but no dietary preference recorded.
        var ctrl = BuildSut(userId, MakeUserInfo(userId, "B", "F", "L", dietary: null));

        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(Event);
        StubToggleOutcome(ToggleDaySignupOutcome.DietaryRequired());

        var result = await ctrl.ToggleDay(shiftId, Xunit.TestContext.Current.CancellationToken);

        var status = result.Should().BeAssignableTo<IStatusCodeActionResult>().Subject;
        status.StatusCode.Should().Be(204);
        await _signupService.Received(1).ToggleDayAsync(
            userId, shiftId, Event.Id, /* privileged */ false, /* hasDietaryPreference */ false,
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ToggleDay_WhenNameMissing_Returns204_WithRedirectHeader()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        // Empty name fields → name gate trips.
        var ctrl = BuildSut(userId, MakeUserInfo(userId, burner: "", first: "", last: "", dietary: "Vegan"));

        var result = await ctrl.ToggleDay(shiftId, Xunit.TestContext.Current.CancellationToken);

        var status = result.Should().BeAssignableTo<IStatusCodeActionResult>().Subject;
        status.StatusCode.Should().Be(204);
        await _signupService.DidNotReceiveWithAnyArgs().ToggleDayAsync(
            default, default, default, default, default);
    }
}
