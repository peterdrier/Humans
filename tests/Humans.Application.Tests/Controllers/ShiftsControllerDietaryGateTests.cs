using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.DTOs.Shifts;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Testing;
using Humans.Web;
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
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Controllers;

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
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IVolunteerTrackingService _volunteerTrackingService =
        Substitute.For<IVolunteerTrackingService>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly User _user;
    private readonly ShiftsController _controller;
    private readonly ClaimsIdentity _identity;

    public ShiftsControllerDietaryGateTests()
    {
        _user = new User { Id = Guid.NewGuid(), DisplayName = "Test Human", PreferredLanguage = "en" };

        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        // The controller resolves the current user via IUserService.GetUserInfoAsync,
        // and reads DietaryPreference straight off UserInfo.Profile. Default: empty
        // dietary; tests override via SetDietary.
        SetDietary(null);

        // Mine() reads shiftView.GetUserAsync(...).Signups (#720); return an
        // empty, event-less view so the Mine-flag tests reach the dietary
        // computation without a full shift-graph fixture.
        _shiftView.GetUserAsync(_user.Id, Arg.Any<CancellationToken>())
            .Returns(new ShiftUserView(_user.Id, null, null, null, [], []));

        var builder = new ShiftBrowsePageBuilder(_shiftMgmt, _teamService);

        _controller = new ShiftsController(
            _shiftMgmt,
            _signupService,
            _volunteerTrackingService,
            _shiftView,
            _teamService,
            _auditLogService,
            _userService,
            localizer,
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
    // passes) with the given DietaryPreference (the dietary gate's input).
    private void SetDietary(string? dietary) =>
        _userService.GetUserInfoAsync(_user.Id, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(
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
                    DietaryPreference = dietary,
                    CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                    UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                },
                contactFields: [],
                profileLanguages: [],
                volunteerHistory: [],
                communicationPreferences: []));

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

        var model = await GetMineViewModel();

        model.UserId.Should().Be(_user.Id);
        model.SignupsBlockedByMissingDietary.Should().BeFalse();
    }

    [HumansFact]
    public async Task Mine_QualifyingSignup_DietaryEmpty_FlagTrue()
    {
        _shiftMgmt.HasQualifyingCantinaSignupAsync(_user.Id, Arg.Any<CancellationToken>())
                  .Returns(true);
        SetDietary(null);

        var model = await GetMineViewModel();

        model.SignupsBlockedByMissingDietary.Should().BeTrue();
    }

    [HumansFact]
    public async Task Mine_QualifyingSignup_DietaryFilled_FlagFalse()
    {
        _shiftMgmt.HasQualifyingCantinaSignupAsync(_user.Id, Arg.Any<CancellationToken>())
                  .Returns(true);
        SetDietary("Vegan");

        var model = await GetMineViewModel();

        model.SignupsBlockedByMissingDietary.Should().BeFalse();
    }

    private async Task<Humans.Web.Models.MyShiftsViewModel> GetMineViewModel()
    {
        var result = await _controller.Mine();
        return result.Should().BeOfType<ViewResult>()
                     .Which.Model.Should().BeOfType<Humans.Web.Models.MyShiftsViewModel>().Subject;
    }
}
