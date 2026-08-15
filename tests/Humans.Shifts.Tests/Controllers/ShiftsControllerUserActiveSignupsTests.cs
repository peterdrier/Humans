using Humans.Shifts.Domain;
using System.Security.Claims;
using Humans.Onboarding;
using Humans.Application;
using Humans.Shifts.Services.Dtos;
using Humans.AuditLog.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
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

namespace Humans.Shifts.Tests.Controllers;

/// <summary>
/// The browse page's conflict strip (<see cref="ShiftBrowseViewModel.UserActiveSignups"/>)
/// renders each signup against the burn its own rota belongs to, not the active one.
/// A user's signup history spans cycles, so a single-burn fixture would pass even if
/// the controller wrongly resolved every row through <c>GetActiveAsync()</c> — these
/// tests deliberately straddle two burns in different timezones (issue #809).
/// </summary>
public class ShiftsControllerUserActiveSignupsTests
{
    private static readonly Guid ActiveBurnId = Guid.NewGuid();
    private static readonly Guid PastBurnId = Guid.NewGuid();

    // Madrid (UTC+2 in July) vs Auckland (UTC+13 in January). If the wrong burn were
    // used for a row, both the local time-of-day and the absolute instant would move.
    private static readonly BurnSettingsInfo ActiveBurn =
        MakeBurn(ActiveBurnId, "Nowhere 2026", 2026, "Europe/Madrid", new LocalDate(2026, 7, 9));

    private static readonly BurnSettingsInfo PastBurn =
        MakeBurn(PastBurnId, "Southern 2025", 2025, "Pacific/Auckland", new LocalDate(2025, 1, 15));

    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly IShiftSignupService _signupService = Substitute.For<IShiftSignupService>();
    private readonly IVolunteerTrackingService _volunteerTracking = Substitute.For<IVolunteerTrackingService>();
    private readonly IShiftRowView _shiftView = Substitute.For<IShiftRowView>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IStringLocalizer<ShiftsResource> _localizer = Substitute.For<IStringLocalizer<ShiftsResource>>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly Guid _userId = Guid.NewGuid();

    public ShiftsControllerUserActiveSignupsTests()
    {
        _localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        _clock.GetCurrentInstant().Returns(Instant.FromUtc(2026, 6, 1, 0, 0));

        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(ActiveBurn);
        _burnSettings.GetByIdAsync(ActiveBurnId, Arg.Any<CancellationToken>()).Returns(ActiveBurn);
        _burnSettings.GetByIdAsync(PastBurnId, Arg.Any<CancellationToken>()).Returns(PastBurn);

        _shiftMgmt.GetCoordinatorTeamIdsAsync(_userId).Returns([]);
        _shiftMgmt.GetBrowseShiftsAsync(Arg.Any<ShiftBrowseQuery>()).Returns([]);
        _shiftMgmt.GetTagsAsync().Returns([]);
        _shiftMgmt.GetDepartmentCoveragePiesAsync(
            Arg.Any<Guid>(), Arg.Any<LocalDate?>(), Arg.Any<LocalDate?>(), ct: Arg.Any<CancellationToken>())
            .Returns([]);
        _shiftMgmt.HasQualifyingCantinaSignupAsync(_userId, Arg.Any<CancellationToken>()).Returns(false);
        _teamService.GetTeamsAsync().Returns(new Dictionary<Guid, TeamInfo>());

        _shiftView.GetUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new ShiftUserView(_userId, null, null, null, [], []));
    }

    [HumansFact]
    public async Task Index_SignupsSpanningTwoBurns_ResolvesEachRowAgainstItsOwnBurn()
    {
        // Same DayOffset + StartTime in both burns: any difference in the rendered
        // instants can only come from the burn each row was resolved against.
        _signupService.GetByUserAsync(_userId).Returns(
        [
            MakeSignup(ActiveBurnId, "Gate 2026", dayOffset: 1, new LocalTime(10, 0), SignupStatus.Confirmed),
            MakeSignup(PastBurnId, "Bar 2025", dayOffset: 1, new LocalTime(10, 0), SignupStatus.Pending),
        ]);

        var model = await BuildBrowseModelAsync();

        Assert.Equal(2, model.UserActiveSignups.Count);

        var current = model.UserActiveSignups[0];
        Assert.Equal("Gate 2026", current.RotaName);
        Assert.Equal(new LocalDate(2026, 7, 10), current.Date);
        // 2026-07-10 10:00 Europe/Madrid = 08:00Z
        Assert.Equal(Instant.FromUtc(2026, 7, 10, 8, 0), current.AbsoluteStart);

        var past = model.UserActiveSignups[1];
        Assert.Equal("Bar 2025", past.RotaName);
        Assert.Equal(new LocalDate(2025, 1, 16), past.Date);
        // 2025-01-16 10:00 Pacific/Auckland = 2025-01-15 21:00Z
        Assert.Equal(Instant.FromUtc(2025, 1, 15, 21, 0), past.AbsoluteStart);

        // Both rows share one burn id each, so the distinct set is resolved once per
        // burn — never once per row (that would be an N+1).
        await _burnSettings.Received(1).GetByIdAsync(ActiveBurnId, Arg.Any<CancellationToken>());
        await _burnSettings.Received(1).GetByIdAsync(PastBurnId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Index_ManyRowsInOneBurn_ResolvesThatBurnOnce()
    {
        _signupService.GetByUserAsync(_userId).Returns(
        [
            MakeSignup(ActiveBurnId, "A", dayOffset: 0, new LocalTime(9, 0), SignupStatus.Confirmed),
            MakeSignup(ActiveBurnId, "B", dayOffset: 1, new LocalTime(9, 0), SignupStatus.Confirmed),
            MakeSignup(ActiveBurnId, "C", dayOffset: 2, new LocalTime(9, 0), SignupStatus.Pending),
        ]);

        var model = await BuildBrowseModelAsync();

        Assert.Equal(3, model.UserActiveSignups.Count);
        await _burnSettings.Received(1).GetByIdAsync(ActiveBurnId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Index_InactiveStatusesAndUnresolvableBurns_AreDropped()
    {
        var unknownBurnId = Guid.NewGuid();
        _burnSettings.GetByIdAsync(unknownBurnId, Arg.Any<CancellationToken>()).Returns((BurnSettingsInfo?)null);

        _signupService.GetByUserAsync(_userId).Returns(
        [
            MakeSignup(ActiveBurnId, "Keeper", dayOffset: 0, new LocalTime(9, 0), SignupStatus.Confirmed),
            MakeSignup(ActiveBurnId, "Bailed", dayOffset: 0, new LocalTime(9, 0), SignupStatus.Bailed),
            MakeSignup(unknownBurnId, "Orphan", dayOffset: 0, new LocalTime(9, 0), SignupStatus.Confirmed),
        ]);

        var model = await BuildBrowseModelAsync();

        Assert.Equal("Keeper", Assert.Single(model.UserActiveSignups).RotaName);
    }

    private async Task<ShiftBrowseViewModel> BuildBrowseModelAsync()
    {
        var ctrl = BuildSut();
        var result = await ctrl.Index(departmentId: null, fromDate: null, toDate: null, period: null);
        return Assert.IsType<ShiftBrowseViewModel>(Assert.IsType<ViewResult>(result).Model);
    }

    private ShiftsController BuildSut()
    {
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(MakeUserInfo(_userId));

        var ctrl = new ShiftsController(
            _shiftMgmt, _burnSettings, _signupService, _volunteerTracking, _shiftView, _teamService,
            _auditLog, _userService, _localizer, Substitute.For<IStringLocalizer<OnboardingResource>>(), _clock,
            new ShiftBrowsePageBuilder(_shiftMgmt, _burnSettings, _teamService),
            NullLogger<ShiftsController>.Instance);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "test")),
        };
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        http.RequestServices = services.BuildServiceProvider();
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        ctrl.Url = Substitute.For<IUrlHelper>();
        return ctrl;
    }

    // Rota carries only EventSettingsId — the EventSettings navigation is deliberately
    // left unpopulated so a regression back to reading it off the graph fails here.
    private static ShiftSignup MakeSignup(
        Guid burnId, string rotaName, int dayOffset, LocalTime start, SignupStatus status) =>
        new()
        {
            Id = Guid.NewGuid(),
            Status = status,
            Shift = new Shift
            {
                Id = Guid.NewGuid(),
                DayOffset = dayOffset,
                StartTime = start,
                Duration = Duration.FromHours(4),
                Rota = new Rota { Id = Guid.NewGuid(), EventSettingsId = burnId, Name = rotaName },
            },
        };

    private static BurnSettingsInfo MakeBurn(
        Guid id, string name, int year, string timeZoneId, LocalDate gateOpening) =>
        new(
            Id: id,
            EventName: name,
            Year: year,
            TimeZoneId: timeZoneId,
            GateOpeningDate: gateOpening,
            BuildStartOffset: -7,
            EventEndOffset: 4,
            StrikeEndOffset: 6,
            FirstCrewStartOffset: -25,
            SetupWeekStartOffset: -16,
            PreEventWeekStartOffset: -9,
            FinishingWeekendStartOffset: -4,
            EarlyEntryCapacity: new Dictionary<int, int>(),
            BarriosEarlyEntryAllocation: null,
            EarlyEntryClose: null,
            IsShiftBrowsingOpen: true);

    private static UserInfo MakeUserInfo(Guid userId) =>
        UserInfo.Create(
            user: new User
            {
                Id = userId,
                DisplayName = "Burner",
                PreferredLanguage = "en",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: new Profile
            {
                UserId = userId,
                BurnerName = "Burner",
                FirstName = "First",
                LastName = "Last",
                DietaryPreference = "None",
                State = ProfileState.Active,
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            },
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
}
