using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Testing;
using Humans.Web;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
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
    private readonly IGeneralAvailabilityService _availabilityService =
        Substitute.For<IGeneralAvailabilityService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly UserManager<User> _userManager;
    private readonly User _user;
    private readonly ShiftsController _controller;
    private readonly ClaimsIdentity _identity;

    public ShiftsControllerDietaryGateTests()
    {
        _user = new User { Id = Guid.NewGuid(), DisplayName = "Test Human", PreferredLanguage = "en" };

        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(_user);

        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        _controller = new ShiftsController(
            _shiftMgmt,
            _signupService,
            _availabilityService,
            _teamService,
            _auditLogService,
            localizer,
            _userManager,
            new FakeClock(Instant.FromUtc(2026, 5, 25, 12, 0)),
            NullLogger<ShiftsController>.Instance);

        _identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _user.Id.ToString()),
        }, authenticationType: "TestAuth");
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(_identity) };
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        _controller.TempData = new TempDataDictionary(httpContext, Substitute.For<ITempDataProvider>());
    }

    [HumansFact]
    public async Task SignUp_DietaryEmpty_QualifyingShift_RedirectsToDietaryMedical()
    {
        var shiftId = Guid.NewGuid();
        _shiftMgmt.GetShiftByIdAsync(shiftId).Returns(BuildShift(shiftId, qualifiesForCantina: true));
        _shiftMgmt.GetShiftProfileAsync(_user.Id, includeMedical: false)
                  .Returns(new VolunteerEventProfile { UserId = _user.Id, DietaryPreference = null });

        var result = await _controller.SignUp(shiftId, null, null, null, null, null, null, null);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("DietaryMedical");
        redirect.ControllerName.Should().Be("Profile");
        redirect.RouteValues!["returnAction"].Should().Be("signup");
        redirect.RouteValues["shiftId"].Should().Be(shiftId);
        await _signupService.DidNotReceive()
            .SignUpAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<bool>());
    }

    [HumansFact]
    public async Task SignUp_DietaryEmpty_NonQualifyingShift_ProceedsToSignup()
    {
        var shiftId = Guid.NewGuid();
        _shiftMgmt.GetShiftByIdAsync(shiftId).Returns(BuildShift(shiftId, qualifiesForCantina: false));
        _shiftMgmt.GetShiftProfileAsync(_user.Id, includeMedical: false)
                  .Returns(new VolunteerEventProfile { UserId = _user.Id, DietaryPreference = null });
        _signupService.SignUpAsync(_user.Id, shiftId, Arg.Any<Guid?>(), Arg.Any<bool>())
                      .Returns(SignupResult.Ok(new ShiftSignup { Id = Guid.NewGuid() }));

        var result = await _controller.SignUp(shiftId, null, null, null, null, null, null, null);

        await _signupService.Received(1)
            .SignUpAsync(_user.Id, shiftId, Arg.Any<Guid?>(), Arg.Any<bool>());
        result.Should().BeOfType<RedirectToActionResult>()
              .Which.ActionName.Should().Be(nameof(ShiftsController.Index));
    }

    [HumansFact]
    public async Task SignUp_DietaryFilled_QualifyingShift_ProceedsToSignup()
    {
        var shiftId = Guid.NewGuid();
        _shiftMgmt.GetShiftByIdAsync(shiftId).Returns(BuildShift(shiftId, qualifiesForCantina: true));
        _shiftMgmt.GetShiftProfileAsync(_user.Id, includeMedical: false)
                  .Returns(new VolunteerEventProfile { UserId = _user.Id, DietaryPreference = "Vegan" });
        _signupService.SignUpAsync(_user.Id, shiftId, Arg.Any<Guid?>(), Arg.Any<bool>())
                      .Returns(SignupResult.Ok(new ShiftSignup { Id = Guid.NewGuid() }));

        var result = await _controller.SignUp(shiftId, null, null, null, null, null, null, null);

        await _signupService.Received(1)
            .SignUpAsync(_user.Id, shiftId, Arg.Any<Guid?>(), Arg.Any<bool>());
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
        _shiftMgmt.GetShiftProfileAsync(_user.Id, includeMedical: false)
                  .Returns(new VolunteerEventProfile { UserId = _user.Id, DietaryPreference = null });

        var result = await _controller.SignUp(shiftId, null, null, null, null, null, null, null);

        result.Should().BeOfType<RedirectToActionResult>()
              .Which.ActionName.Should().Be("DietaryMedical");
        await _signupService.DidNotReceive()
            .SignUpAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<bool>());
    }

    [HumansFact]
    public async Task SignUpRange_DietaryEmpty_RangeHasQualifyingShift_RedirectsToDietaryMedical()
    {
        var rotaId = Guid.NewGuid();
        _signupService.PeekRangeShiftsAsync(rotaId, 0, 2, Arg.Any<CancellationToken>())
                      .Returns(new[] { BuildShift(Guid.NewGuid(), qualifiesForCantina: true) });
        _shiftMgmt.GetShiftProfileAsync(_user.Id, includeMedical: false)
                  .Returns(new VolunteerEventProfile { UserId = _user.Id, DietaryPreference = null });

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
            Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<bool>());
    }

    [HumansFact]
    public async Task SignUpRange_DietaryEmpty_RangeEmpty_ProceedsToSignup()
    {
        var rotaId = Guid.NewGuid();
        _signupService.PeekRangeShiftsAsync(rotaId, 0, 2, Arg.Any<CancellationToken>())
                      .Returns(Array.Empty<Shift>());
        _shiftMgmt.GetShiftProfileAsync(_user.Id, includeMedical: false)
                  .Returns(new VolunteerEventProfile { UserId = _user.Id, DietaryPreference = null });
        _signupService.SignUpRangeAsync(_user.Id, rotaId, 0, 2, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<bool>())
                      .Returns(SignupResult.Ok(new ShiftSignup { Id = Guid.NewGuid() }));

        var result = await _controller.SignUpRange(rotaId, 0, 2, null, null, null, null, null, null, null);

        await _signupService.Received(1)
            .SignUpRangeAsync(_user.Id, rotaId, 0, 2, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<bool>());
        result.Should().BeOfType<RedirectToActionResult>()
              .Which.ActionName.Should().Be(nameof(ShiftsController.Index));
    }

    // All-day shifts qualify; this is the simplest knob to flip the
    // QualifiesForCantinaMeal() check without fabricating Duration values.
    private static Shift BuildShift(Guid id, bool qualifiesForCantina) =>
        new() { Id = id, IsAllDay = qualifiesForCantina };
}
