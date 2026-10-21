using Humans.Cantina.Contracts;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Onboarding;
using Humans.Shifts.Services.Dtos;
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
using NodaTime.Testing;
using NSubstitute;
using Humans.Users.Contracts;
using Humans.Users.Domain;
using Humans.Users.Services;

namespace Humans.Shifts.Tests.Controllers;

/// <summary>
/// Coverage for the dietary-blocked flag surfaced on My Shifts. Mine() calls
/// ComputeSignupsBlockedByMissingDietaryAsync, which sets
/// MyShiftsViewModel.SignupsBlockedByMissingDietary true only when the user has a
/// qualifying cantina signup (all-day or ≥6h) AND an empty DietaryPreference. The
/// rota-table Sign-Up buttons read this flag to nudge the user to the dietary form.
/// </summary>
public class ShiftsControllerDietaryGateTests
{
    private readonly IShiftSignupService _signupService = Substitute.For<IShiftSignupService>();
    private readonly IShiftManagement _shiftMgmt = Substitute.For<IShiftManagement>();
    private readonly IVolunteerTrackingManagement _volunteerTrackingService =
        Substitute.For<IVolunteerTrackingManagement>();
    private readonly IShiftRowView _shiftView = Substitute.For<IShiftRowView>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly ICantinaService _cantina = Substitute.For<ICantinaService>();
    private readonly User _user;
    private readonly ShiftsController _controller;
    private readonly ClaimsIdentity _identity;

    public ShiftsControllerDietaryGateTests()
    {
        _user = new User { Id = Guid.NewGuid(), DisplayName = "Test Human", PreferredLanguage = "en" };

        var localizer = Substitute.For<IStringLocalizer<ShiftsResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        // The controller resolves the current user via IUserService.GetUserInfoAsync
        // and reads DietaryPreference from ICantinaService. Default: empty dietary;
        // tests override via SetDietary.
        SetDietary(null);

        // Mine() reads shiftView.GetUserAsync(...).Signups (nobodies-collective/Humans#720); return an
        // empty, event-less view so the Mine-flag tests reach the dietary
        // computation without a full shift-graph fixture.
        _shiftView.GetUserAsync(_user.Id, Arg.Any<CancellationToken>())
            .Returns(new ShiftUserView(_user.Id, null, null, null, [], []));

        var burnSettings = Substitute.For<IBurnSettingsService>();
        var builder = new ShiftBrowsePageBuilder(_shiftMgmt, burnSettings, _teamService);

        _controller = new ShiftsController(
            _shiftMgmt,
            burnSettings,
            _signupService,
            _volunteerTrackingService,
            _shiftView,
            _teamService,
            _auditLogService,
            _userService,
            _cantina,
            localizer,
            Substitute.For<IStringLocalizer<OnboardingResource>>(),
            new FakeClock(Instant.FromUtc(2026, 5, 25, 12, 0)),
            builder,
            NullLogger<ShiftsController>.Instance);

        _identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _user.Id.ToString()),
        }, authenticationType: "TestAuth");
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(_identity) };
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        httpContext.RequestServices = services.BuildServiceProvider();
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        _controller.TempData = new TempDataDictionary(httpContext, Substitute.For<ITempDataProvider>());
        _controller.Url = Substitute.For<IUrlHelper>();
    }

    // Stubs GetUserInfoAsync to return a name-complete profile (so the name gate
    // passes), and Cantina's dietary profile with the given DietaryPreference (the gate's input).
    private void SetDietary(string? dietary)
    {
        _cantina.GetDietaryProfileAsync(_user.Id, Arg.Any<CancellationToken>())
            .Returns(dietary is null ? null : new DietaryProfileInfo(_user.Id, dietary, [], null, [], null));
        _userService.GetUserInfoAsync(_user.Id, Arg.Any<CancellationToken>())
            .Returns(UserInfoFactory.Create(
                user: _user,
                userEmails: [],
                eventParticipations: [],
                externalLogins: [],
                profile: new Profile
                {
                    UserId = _user.Id,
                    BurnerName = "Burner",
                    FirstName = "First",
                    LastName = "Last",
                    CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                    UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                },
                contactFields: [],
                profileLanguages: [],
                volunteerHistory: [],
                communicationPreferences: []));
    }

    // ---- Lockout-flag computation on the top-level VM (Task 6.2) ----
    // ComputeSignupsBlockedByMissingDietaryAsync sets the flag the rota-table
    // Sign-Up buttons read. Mine() is the simplest action to drive: it tolerates
    // a null active event and empty signups, so the three flag states isolate
    // cleanly. (Index() would need a full shift-graph fixture.)

    [HumansFact]
    public async Task Mine_NoQualifyingCantinaSignup_FlagFalse()
    {
        _shiftMgmt.HasQualifyingCantinaSignupAsync(_user.Id, Arg.Any<CancellationToken>())
                  .Returns(false);

        var model = await GetMineViewModelAsync();

        model.UserId.Should().Be(_user.Id);
        model.SignupsBlockedByMissingDietary.Should().BeFalse();
    }

    [HumansFact]
    public async Task Mine_QualifyingSignup_DietaryEmpty_FlagTrue()
    {
        _shiftMgmt.HasQualifyingCantinaSignupAsync(_user.Id, Arg.Any<CancellationToken>())
                  .Returns(true);
        SetDietary(null);

        var model = await GetMineViewModelAsync();

        model.SignupsBlockedByMissingDietary.Should().BeTrue();
    }

    [HumansFact]
    public async Task Mine_QualifyingSignup_DietaryFilled_FlagFalse()
    {
        _shiftMgmt.HasQualifyingCantinaSignupAsync(_user.Id, Arg.Any<CancellationToken>())
                  .Returns(true);
        SetDietary("Vegan");

        var model = await GetMineViewModelAsync();

        model.SignupsBlockedByMissingDietary.Should().BeFalse();
    }

    private async Task<MyShiftsViewModel> GetMineViewModelAsync()
    {
        var result = await _controller.Mine();
        return result.Should().BeOfType<ViewResult>()
                     .Which.Model.Should().BeOfType<MyShiftsViewModel>().Subject;
    }
}
