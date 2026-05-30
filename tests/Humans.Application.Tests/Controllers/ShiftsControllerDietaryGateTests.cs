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
/// Coverage for ShiftsController.SignUp's dietary-info gate. Before delegating
/// to ShiftSignupService.SignUpAsync, the controller checks whether the target
/// shift qualifies for a cantina meal (all-day or ≥6h) and the user's
/// DietaryPreference is empty. If both true, the user is redirected to the
/// DietaryMedical form with returnAction=signup&amp;shiftId so the form-completion
/// handler can replay the signup.
///
/// The privileged-actor case is load-bearing: privileged signup approvers can
/// bypass capacity validation but MUST still pass through the dietary gate when
/// signing themselves up — otherwise admins doing self-signup on long shifts
/// would skip the very nudge the feature exists to deliver.
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

    [HumansFact]
    public async Task SignUp_DietaryEmpty_QualifyingShift_RedirectsToDietaryMedical()
    {
        var shiftId = Guid.NewGuid();
        _shiftMgmt.GetShiftByIdAsync(shiftId).Returns(BuildShift(shiftId, qualifiesForCantina: true));
        SetDietary(null);

        var result = await _controller.SignUp(shiftId, null, null, null, null, null, null, null);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("DietaryMedical");
        redirect.ControllerName.Should().Be("Profile");
        redirect.RouteValues!["returnAction"].Should().Be("signup");
        redirect.RouteValues["shiftId"].Should().Be(shiftId);
        await _signupService.DidNotReceive()
            .SignUpAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<Guid?>(),
                Arg.Any<ShiftSignupRequestFlags>());
    }

    [HumansFact]
    public async Task SignUp_DietaryEmpty_NonQualifyingShift_ProceedsToSignup()
    {
        var shiftId = Guid.NewGuid();
        _shiftMgmt.GetShiftByIdAsync(shiftId).Returns(BuildShift(shiftId, qualifiesForCantina: false));
        SetDietary(null);
        _signupService.SignUpAsync(_user.Id, shiftId, Arg.Any<Guid?>(), Arg.Any<ShiftSignupRequestFlags>())
                      .Returns(SignupResult.Ok(new ShiftSignup { Id = Guid.NewGuid() }));

        var result = await _controller.SignUp(shiftId, null, null, null, null, null, null, null);

        await _signupService.Received(1)
            .SignUpAsync(_user.Id, shiftId, Arg.Any<Guid?>(), Arg.Any<ShiftSignupRequestFlags>());
        result.Should().BeOfType<RedirectToActionResult>()
              .Which.ActionName.Should().Be(nameof(ShiftsController.Index));
    }

    [HumansFact]
    public async Task SignUp_DietaryFilled_QualifyingShift_ProceedsToSignup()
    {
        var shiftId = Guid.NewGuid();
        _shiftMgmt.GetShiftByIdAsync(shiftId).Returns(BuildShift(shiftId, qualifiesForCantina: true));
        SetDietary("Vegan");
        _signupService.SignUpAsync(_user.Id, shiftId, Arg.Any<Guid?>(), Arg.Any<ShiftSignupRequestFlags>())
                      .Returns(SignupResult.Ok(new ShiftSignup { Id = Guid.NewGuid() }));

        var result = await _controller.SignUp(shiftId, null, null, null, null, null, null, null);

        await _signupService.Received(1)
            .SignUpAsync(_user.Id, shiftId, Arg.Any<Guid?>(), Arg.Any<ShiftSignupRequestFlags>());
        result.Should().BeOfType<RedirectToActionResult>()
              .Which.ActionName.Should().Be(nameof(ShiftsController.Index));
    }

    [HumansFact]
    public async Task SignUp_DietaryEmpty_QualifyingShift_PrivilegedActor_StillRedirects()
    {
        // The actor IS the user being signed up in ShiftsController.SignUp.
        // Privileged-actor only relaxes signup validation; it does not bypass the
        // dietary gate.
        var shiftId = Guid.NewGuid();
        _identity.AddClaim(new Claim(ClaimTypes.Role, RoleNames.Admin));

        _shiftMgmt.GetShiftByIdAsync(shiftId).Returns(BuildShift(shiftId, qualifiesForCantina: true));
        SetDietary(null);

        var result = await _controller.SignUp(shiftId, null, null, null, null, null, null, null);

        result.Should().BeOfType<RedirectToActionResult>()
              .Which.ActionName.Should().Be("DietaryMedical");
        await _signupService.DidNotReceive()
            .SignUpAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<Guid?>(),
                Arg.Any<ShiftSignupRequestFlags>());
    }

    [HumansFact]
    public async Task SignUpRange_DietaryEmpty_RangeHasQualifyingShift_RedirectsToDietaryMedical()
    {
        var rotaId = Guid.NewGuid();
        SetRotaView(rotaId, BuildShift(Guid.NewGuid(), qualifiesForCantina: true));
        SetDietary(null);

        var result = await _controller.SignUpRange(rotaId, 0, 2, null, null, null, null, null, null, null);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("DietaryMedical");
        redirect.ControllerName.Should().Be("Profile");
        redirect.RouteValues!["returnAction"].Should().Be("signuprange");
        redirect.RouteValues["rotaId"].Should().Be(rotaId);
        redirect.RouteValues["startDayOffset"].Should().Be(0);
        redirect.RouteValues["endDayOffset"].Should().Be(2);
        await _signupService.DidNotReceive().SignUpRangeAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<Guid?>(), Arg.Any<ShiftSignupRequestFlags>());
    }

    [HumansFact]
    public async Task SignUpRange_DietaryEmpty_RangeEmpty_ProceedsToSignup()
    {
        var rotaId = Guid.NewGuid();
        SetRotaView(rotaId);
        SetDietary(null);
        _signupService.SignUpRangeAsync(
                _user.Id,
                rotaId,
                0,
                2,
                Arg.Any<Guid?>(),
                Arg.Any<ShiftSignupRequestFlags>())
                      .Returns(SignupResult.Ok(new ShiftSignup { Id = Guid.NewGuid() }));

        var result = await _controller.SignUpRange(rotaId, 0, 2, null, null, null, null, null, null, null);

        await _signupService.Received(1)
            .SignUpRangeAsync(
                _user.Id,
                rotaId,
                0,
                2,
                Arg.Any<Guid?>(),
                Arg.Any<ShiftSignupRequestFlags>());
        result.Should().BeOfType<RedirectToActionResult>()
              .Which.ActionName.Should().Be(nameof(ShiftsController.Index));
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

    private void SetRotaView(Guid rotaId, params Shift[] shifts) =>
        _shiftView.GetRotaAsync(rotaId, Arg.Any<CancellationToken>())
            .Returns(new ShiftRotaView(rotaId, new Rota { Id = rotaId }, shifts, [], []));

    // All-day shifts qualify; this is the simplest knob to flip the
    // QualifiesForCantinaMeal() check without fabricating Duration values.
    private static Shift BuildShift(Guid id, bool qualifiesForCantina) =>
        new() { Id = id, IsAllDay = qualifiesForCantina };
}
