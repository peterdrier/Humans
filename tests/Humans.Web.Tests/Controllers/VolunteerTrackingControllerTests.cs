using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Constants;
using Humans.Web.Controllers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
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
/// Verifies <see cref="VolunteerTrackingController"/> wiring:
/// authorization attributes (read = ShiftDashboardAccess, write =
/// VolunteerTrackingWrite), Index sort/filter/empty-event behavior,
/// and the write actions (SetCampSetup / ClearCampSetup / SetDayOff /
/// ClearDayOff / SetAvailabilityDay / ClearAvailabilityDay): success
/// → 302 + audit, validation failures → no audit, service rejection
/// → error TempData, and returnUrl honored when local.
///
/// This is a unit-test project (no Testcontainers/WebApplicationFactory),
/// so policy enforcement is verified by reflection on the
/// <c>[Authorize(Policy = …)]</c> attributes — the policies themselves are
/// covered by separate authorization tests under
/// <c>tests/Humans.Web.Tests/Authorization/</c>.
/// </summary>
public class VolunteerTrackingControllerTests
{
    private readonly UserManager<User> _userManager;
    private readonly IVolunteerTrackingService _service = Substitute.For<IVolunteerTrackingService>();
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IVolunteerTrackingExportService _exportService =
        Substitute.For<IVolunteerTrackingExportService>();
    private readonly Humans.Web.Models.VolunteerTracking.VolunteerTrackingXlsxBuilder _xlsxBuilder = new();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();
    private readonly IStringLocalizer<SharedResource> _localizer =
        Substitute.For<IStringLocalizer<SharedResource>>();

    public VolunteerTrackingControllerTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
        _localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
    }

    private VolunteerTrackingController BuildSut(User? currentUser)
    {
        if (currentUser is not null)
        {
            _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(currentUser);
            _userService.GetUserInfoAsync(currentUser.Id, Arg.Any<CancellationToken>())
                .Returns(new ValueTask<UserInfo?>(UserInfo.Create(
                    currentUser,
                    [],
                    [],
                    [],
                    profile: null,
                    [],
                    [],
                    [],
                    [])));
        }

        var ctrl = new VolunteerTrackingController(
            _service, _shiftMgmt, _exportService, _xlsxBuilder,
            _userService, _auditLog, _localizer);

        var http = new DefaultHttpContext();
        if (currentUser is not null)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, currentUser.Id.ToString())
                ],
                "test"));
        }

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var urlHelperFactory = Substitute.For<IUrlHelperFactory>();
        urlHelperFactory.GetUrlHelper(Arg.Any<ActionContext>())
            .Returns(Substitute.For<IUrlHelper>());
        services.AddSingleton(urlHelperFactory);
        http.RequestServices = services.BuildServiceProvider();

        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor
            {
                ActionName = "Test",
            },
        };
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        ctrl.Url = Substitute.For<IUrlHelper>();
        return ctrl;
    }

    // ---------------------------------------------------------------------
    // Authorization — class-level read policy + per-action write policies.
    // The integration story (anonymous redirect / role-rejected 403 / VC 200)
    // is enforced by ASP.NET's policy pipeline; here we verify the wiring
    // is in place so the framework will enforce it.
    // ---------------------------------------------------------------------

    [HumansFact]
    public void Class_RequiresShiftDashboardAccess_Policy()
    {
        var attr = typeof(VolunteerTrackingController)
            .GetCustomAttribute<AuthorizeAttribute>();
        attr.Should().NotBeNull("class-level [Authorize] gates anonymous reads");
        attr.Policy.Should().Be(PolicyNames.ShiftDashboardAccess);
    }

    [HumansTheory]
    [InlineData(nameof(VolunteerTrackingController.SetCampSetup))]
    [InlineData(nameof(VolunteerTrackingController.ClearCampSetup))]
    [InlineData(nameof(VolunteerTrackingController.SetDayOff))]
    [InlineData(nameof(VolunteerTrackingController.ClearDayOff))]
    public void WriteActions_Require_VolunteerTrackingWrite_Policy(string actionName)
    {
        var method = typeof(VolunteerTrackingController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => string.Equals(m.Name, actionName, StringComparison.Ordinal));
        var attr = method.GetCustomAttribute<AuthorizeAttribute>();
        attr.Should().NotBeNull($"{actionName} must require VolunteerTrackingWrite");
        attr.Policy.Should().Be(PolicyNames.VolunteerTrackingWrite);
    }

    [HumansTheory]
    [InlineData(nameof(VolunteerTrackingController.SetCampSetup))]
    [InlineData(nameof(VolunteerTrackingController.ClearCampSetup))]
    [InlineData(nameof(VolunteerTrackingController.SetDayOff))]
    [InlineData(nameof(VolunteerTrackingController.ClearDayOff))]
    public void WriteActions_Have_AntiForgery_Validation(string actionName)
    {
        var method = typeof(VolunteerTrackingController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => string.Equals(m.Name, actionName, StringComparison.Ordinal));
        var attr = method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>();
        attr.Should().NotBeNull($"{actionName} must validate the anti-forgery token");
    }

    // ---------------------------------------------------------------------
    // Index — empty-event short-circuit.
    // ---------------------------------------------------------------------

    [HumansFact]
    public async Task Index_NoActiveEvent_RendersEmptyViewModel()
    {
        var current = new User { Id = Guid.NewGuid() };
        _service.GetTrackingDataAsync(Arg.Any<CancellationToken>())
            .Returns(new VolunteerTrackingViewModel(
                HasActiveEvent: false,
                BuildStartOffset: 0,
                GateOpeningDate: default,
                Today: default,
                MainCohort: [],
                UnbookedCohort: []));
        var ctrl = BuildSut(current);

        var result = await ctrl.Index(false, false, false, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<VolunteerTrackingPageViewModel>(view.Model);
        model.HasActiveEvent.Should().BeFalse();
        model.MainCohort.Should().BeEmpty();
        model.UnbookedCohort.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------
    // Index — sorting in the controller (display-sort-in-controllers rule).
    // ---------------------------------------------------------------------

    [HumansFact]
    public async Task Index_MainCohort_SortedByGapsThenLastSignupThenName()
    {
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var carolId = Guid.NewGuid();

        // Alice: 2 gaps. Bob: 3 gaps (should sort first). Carol: 2 gaps but
        // earlier last-signup → ties beat Alice.
        var rows = new List<VolunteerHeatmapRow>
        {
            new(aliceId, FirstSignupDay: -10, LastEligibleSignupOffset: -2,
                BarrioSetupStartDate: null, GapCount: 2,
                Cells: [],
                DayOffs: []),
            new(bobId, FirstSignupDay: -10, LastEligibleSignupOffset: -1,
                BarrioSetupStartDate: null, GapCount: 3,
                Cells: [],
                DayOffs: []),
            new(carolId, FirstSignupDay: -10, LastEligibleSignupOffset: -5,
                BarrioSetupStartDate: null, GapCount: 2,
                Cells: [],
                DayOffs: []),
        };
        _service.GetTrackingDataAsync(Arg.Any<CancellationToken>())
            .Returns(new VolunteerTrackingViewModel(true, -10, new LocalDate(2026, 6, 24), new LocalDate(2026, 6, 15), rows,
                []));
        _userService.GetUserInfoAsync(aliceId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(StubUserInfo(aliceId, "Alice")));
        _userService.GetUserInfoAsync(bobId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(StubUserInfo(bobId, "Bob")));
        _userService.GetUserInfoAsync(carolId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(StubUserInfo(carolId, "Carol")));

        var ctrl = BuildSut(new User { Id = Guid.NewGuid() });

        var result = await ctrl.Index(false, false, false, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<VolunteerTrackingPageViewModel>(view.Model);
        model.MainCohort.Select(r => r.UserId).Should().Equal(bobId, carolId, aliceId);
    }

    [HumansFact]
    public async Task Index_HideNoGaps_FiltersZeroGapRows()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var rows = new List<VolunteerHeatmapRow>
        {
            new(a, -10, -2, null, GapCount: 0, [], []),
            new(b, -10, -2, null, GapCount: 1, [], []),
        };
        _service.GetTrackingDataAsync(Arg.Any<CancellationToken>())
            .Returns(new VolunteerTrackingViewModel(true, -10, new LocalDate(2026, 6, 24), new LocalDate(2026, 6, 15), rows,
                []));
        var ctrl = BuildSut(new User { Id = Guid.NewGuid() });

        var result = await ctrl.Index(hideNoGaps: true, false, false, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<VolunteerTrackingPageViewModel>(view.Model);
        model.MainCohort.Should().HaveCount(1);
        model.MainCohort[0].UserId.Should().Be(b);
    }

    [HumansFact]
    public async Task Index_HideCampSetup_FiltersRowsWithBarrioSetupStartDate()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var rows = new List<VolunteerHeatmapRow>
        {
            new(a, -10, -2, BarrioSetupStartDate: new LocalDate(2026, 6, 14),
                GapCount: 5, [], []),
            new(b, -10, -2, null, GapCount: 5, [], []),
        };
        _service.GetTrackingDataAsync(Arg.Any<CancellationToken>())
            .Returns(new VolunteerTrackingViewModel(true, -10, new LocalDate(2026, 6, 24), new LocalDate(2026, 6, 15), rows,
                []));
        var ctrl = BuildSut(new User { Id = Guid.NewGuid() });

        var result = await ctrl.Index(false, hideCampSetup: true, false, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<VolunteerTrackingPageViewModel>(view.Model);
        model.MainCohort.Should().HaveCount(1);
        model.MainCohort[0].UserId.Should().Be(b);
    }

    [HumansFact]
    public async Task Index_HideUnbookedSection_EmptiesUnbookedCohort()
    {
        var u = Guid.NewGuid();
        var unbooked = new List<VolunteerCohortRow>
        {
            new(u, FirstAvailableDay: -3, BarrioSetupStartDate: null,
                UnbookedCount: 2, Cells: []),
        };
        _service.GetTrackingDataAsync(Arg.Any<CancellationToken>())
            .Returns(new VolunteerTrackingViewModel(true, -10, new LocalDate(2026, 6, 24), new LocalDate(2026, 6, 15),
                [], unbooked));
        var ctrl = BuildSut(new User { Id = Guid.NewGuid() });

        var result = await ctrl.Index(false, false, hideUnbookedSection: true, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<VolunteerTrackingPageViewModel>(view.Model);
        model.UnbookedCohort.Should().BeEmpty();
        model.HideUnbookedSection.Should().BeTrue();
    }

    // ---------------------------------------------------------------------
    // SetCampSetup
    // ---------------------------------------------------------------------

    [HumansFact]
    public async Task SetCampSetup_ValidForm_RedirectsAndAuditsAndSetsSuccessTempData()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        _service.SetCampSetupAsync(
                target, Arg.Any<LocalDate>(), Arg.Any<string?>(),
                current.Id, Arg.Any<CancellationToken>())
            .Returns(new SetCampSetupResult(Ok: true, ErrorMessageKey: null, AutoClearedDayOffs: []));
        var ctrl = BuildSut(current);
        var form = new SetCampSetupForm { UserId = target, Date = "2026-06-14", Notes = "early" };

        var result = await ctrl.SetCampSetup(form, null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(VolunteerTrackingController.Index));
        await _auditLog.Received(1).LogAsync(
            AuditAction.VolunteerCampSetupSet,
            nameof(VolunteerBuildStatus),
            target,
            Arg.Any<string>(),
            current.Id,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
        ctrl.TempData[TempDataKeys.SuccessMessage].Should().Be("VolTrack_Msg_CampSetupSaved");
    }

    [HumansFact]
    public async Task SetCampSetup_ModelStateInvalid_RegexFail_RedirectsWithBadRequestError_AndDoesNotAudit()
    {
        var current = new User { Id = Guid.NewGuid() };
        var ctrl = BuildSut(current);
        // Simulate the framework attribute validation: regex on Date failed.
        ctrl.ModelState.AddModelError(nameof(SetCampSetupForm.Date), "regex");
        var form = new SetCampSetupForm
        {
            UserId = Guid.NewGuid(),
            Date = "not-a-date",
            Notes = null,
        };

        var result = await ctrl.SetCampSetup(form, null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(VolunteerTrackingController.Index));
        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("VolTrack_Err_BadRequest");
        await _service.DidNotReceive().SetCampSetupAsync(
            Arg.Any<Guid>(), Arg.Any<LocalDate>(), Arg.Any<string?>(),
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _auditLog.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SetCampSetup_RegexPasses_ButLocalDateInvalid_RedirectsWithBadDateError()
    {
        // Regex matches yyyy-MM-dd but month=13/day=40 fails LocalDatePattern.Iso.
        var current = new User { Id = Guid.NewGuid() };
        var ctrl = BuildSut(current);
        var form = new SetCampSetupForm
        {
            UserId = Guid.NewGuid(),
            Date = "2026-13-40",
            Notes = null,
        };

        var result = await ctrl.SetCampSetup(form, null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(VolunteerTrackingController.Index));
        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("VolTrack_Err_BadDate");
        await _service.DidNotReceive().SetCampSetupAsync(
            Arg.Any<Guid>(), Arg.Any<LocalDate>(), Arg.Any<string?>(),
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SetCampSetup_ServiceRejects_SurfacesLocalizedErrorKey()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        _service.SetCampSetupAsync(
                target, Arg.Any<LocalDate>(), Arg.Any<string?>(),
                current.Id, Arg.Any<CancellationToken>())
            .Returns(new SetCampSetupResult(Ok: false, ErrorMessageKey: "VolTrack_Err_DateOutsideWindow", AutoClearedDayOffs: null));
        var ctrl = BuildSut(current);
        var form = new SetCampSetupForm { UserId = target, Date = "2026-06-14" };

        var result = await ctrl.SetCampSetup(form, null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(VolunteerTrackingController.Index));
        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("VolTrack_Err_DateOutsideWindow");
        await _auditLog.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SetCampSetup_NoCurrentUser_ReturnsForbid()
    {
        // GetCurrentUserAsync returns null when the cookie maps to a deleted/
        // inaccessible identity row even though [Authorize] passed claims.
        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns((User?)null);
        var ctrl = BuildSut(currentUser: null);
        var form = new SetCampSetupForm { UserId = Guid.NewGuid(), Date = "2026-06-14" };

        var result = await ctrl.SetCampSetup(form, null, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    // ---------------------------------------------------------------------
    // ClearCampSetup
    // ---------------------------------------------------------------------

    [HumansFact]
    public async Task ClearCampSetup_HappyPath_RedirectsAndAudits()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        var ctrl = BuildSut(current);

        var result = await ctrl.ClearCampSetup(target, null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(VolunteerTrackingController.Index));
        await _service.Received(1).ClearCampSetupAsync(
            target, current.Id, Arg.Any<CancellationToken>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.VolunteerCampSetupCleared,
            nameof(VolunteerBuildStatus),
            target,
            Arg.Any<string>(),
            current.Id,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
        ctrl.TempData[TempDataKeys.SuccessMessage].Should().Be("VolTrack_Msg_CampSetupCleared");
    }

    [HumansFact]
    public async Task ClearCampSetup_EmptyUserId_DoesNotCallServiceOrAudit()
    {
        var current = new User { Id = Guid.NewGuid() };
        var ctrl = BuildSut(current);

        var result = await ctrl.ClearCampSetup(Guid.Empty, null, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        await _service.DidNotReceive().ClearCampSetupAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _auditLog.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("VolTrack_Err_BadRequest");
    }

    // ---------------------------------------------------------------------
    // SetDayOff / ClearDayOff
    // ---------------------------------------------------------------------

    [HumansFact]
    public async Task SetDayOff_HappyPath_RedirectsAndAuditsMarkedAction()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        _service.SetDayOffAsync(
                target, -3, "doctor", current.Id, Arg.Any<CancellationToken>())
            .Returns(new SetDayOffResult(Ok: true, ErrorMessageKey: null));
        var ctrl = BuildSut(current);
        var form = new SetDayOffForm { UserId = target, DayOffset = -3, Reason = "doctor" };

        var result = await ctrl.SetDayOff(form, null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(VolunteerTrackingController.Index));
        await _auditLog.Received(1).LogAsync(
            AuditAction.VolunteerDayOffMarked,
            nameof(VolunteerBuildStatus),
            target,
            Arg.Any<string>(),
            current.Id,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
        ctrl.TempData[TempDataKeys.SuccessMessage].Should().Be("VolTrack_Msg_DayOffMarked");
    }

    [HumansFact]
    public async Task SetDayOff_ServiceRejects_RedirectsWithErrorTempData_NoAudit()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        _service.SetDayOffAsync(
                target, -3, Arg.Any<string?>(), current.Id, Arg.Any<CancellationToken>())
            .Returns(new SetDayOffResult(Ok: false, ErrorMessageKey: "VolTrack_Err_DayOffWithSignups"));
        var ctrl = BuildSut(current);
        var form = new SetDayOffForm { UserId = target, DayOffset = -3, Reason = null };

        var result = await ctrl.SetDayOff(form, null, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("VolTrack_Err_DayOffWithSignups");
        await _auditLog.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ClearDayOff_HappyPath_RedirectsAndAuditsClearedAction()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        _service.ClearDayOffAsync(
                target, -3, current.Id, Arg.Any<CancellationToken>())
            .Returns(new ClearDayOffResult(Removed: true));
        var ctrl = BuildSut(current);
        var form = new ClearDayOffForm { UserId = target, DayOffset = -3 };

        var result = await ctrl.ClearDayOff(form, null, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        await _auditLog.Received(1).LogAsync(
            AuditAction.VolunteerDayOffCleared,
            nameof(VolunteerBuildStatus),
            target,
            Arg.Any<string>(),
            current.Id,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
        ctrl.TempData[TempDataKeys.SuccessMessage].Should().Be("VolTrack_Msg_DayOffCleared");
    }

    [HumansFact]
    public async Task ClearDayOff_NoEntryToRemove_RedirectsWithoutAudit()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        _service.ClearDayOffAsync(
                target, -3, current.Id, Arg.Any<CancellationToken>())
            .Returns(new ClearDayOffResult(Removed: false));
        var ctrl = BuildSut(current);
        var form = new ClearDayOffForm { UserId = target, DayOffset = -3 };

        var result = await ctrl.ClearDayOff(form, null, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        await _auditLog.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SetCampSetup_FansOutOneAuditPerAutoClearedDayOff()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        _service.SetCampSetupAsync(target, Arg.Any<LocalDate>(), Arg.Any<string?>(), current.Id, Arg.Any<CancellationToken>())
            .Returns(new SetCampSetupResult(
                Ok: true, ErrorMessageKey: null,
                AutoClearedDayOffs: [-6, -4]));
        var ctrl = BuildSut(current);
        var form = new SetCampSetupForm { UserId = target, Date = "2026-06-30", Notes = null };

        await ctrl.SetCampSetup(form, null, CancellationToken.None);

        await _auditLog.Received(1).LogAsync(
            AuditAction.VolunteerCampSetupSet, Arg.Any<string>(), target,
            Arg.Any<string>(), current.Id, Arg.Any<Guid?>(), Arg.Any<string?>());
        await _auditLog.Received(2).LogAsync(
            AuditAction.VolunteerDayOffCleared, Arg.Any<string>(), target,
            Arg.Any<string>(), current.Id, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    // ---------------------------------------------------------------------
    // SetAvailabilityDay / ClearAvailabilityDay
    // ---------------------------------------------------------------------

    [HumansFact]
    public void SetAvailabilityDay_Requires_VolunteerTrackingWrite_Policy()
    {
        var method = typeof(VolunteerTrackingController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => string.Equals(m.Name, nameof(VolunteerTrackingController.SetAvailabilityDay), StringComparison.Ordinal));
        var attr = method.GetCustomAttribute<AuthorizeAttribute>();
        attr.Should().NotBeNull("SetAvailabilityDay must require VolunteerTrackingWrite");
        attr!.Policy.Should().Be(PolicyNames.VolunteerTrackingWrite);
    }

    [HumansFact]
    public void ClearAvailabilityDay_Requires_VolunteerTrackingWrite_Policy()
    {
        var method = typeof(VolunteerTrackingController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => string.Equals(m.Name, nameof(VolunteerTrackingController.ClearAvailabilityDay), StringComparison.Ordinal));
        var attr = method.GetCustomAttribute<AuthorizeAttribute>();
        attr.Should().NotBeNull("ClearAvailabilityDay must require VolunteerTrackingWrite");
        attr!.Policy.Should().Be(PolicyNames.VolunteerTrackingWrite);
    }

    [HumansFact]
    public async Task SetAvailabilityDay_HappyPath_CallsServiceAndAuditsAndRedirects()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        var esId = Guid.NewGuid();
        var es = new Humans.Domain.Entities.EventSettings { Id = esId };
        _shiftMgmt.GetActiveAsync().Returns(es);
        _service
            .SetDayAvailabilityAsync(target, esId, -2, true, Arg.Any<CancellationToken>())
            .Returns(true);
        var ctrl = BuildSut(current);

        var result = await ctrl.SetAvailabilityDay(target, -2, returnUrl: null, CancellationToken.None);

        await _service.Received(1)
            .SetDayAvailabilityAsync(target, esId, -2, true, Arg.Any<CancellationToken>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.VolunteerAvailabilitySet,
            Arg.Any<string>(), target,
            Arg.Any<string>(), current.Id,
            Arg.Any<Guid?>(), Arg.Any<string?>());
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(VolunteerTrackingController.Index));
        ctrl.TempData[TempDataKeys.SuccessMessage].Should().Be("VolTrack_Msg_AvailabilitySet");
    }

    [HumansFact]
    public async Task SetAvailabilityDay_NoChange_DoesNotAudit()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        var es = new Humans.Domain.Entities.EventSettings { Id = Guid.NewGuid() };
        _shiftMgmt.GetActiveAsync().Returns(es);
        // Default: SetDayAvailabilityAsync returns false → no audit row (service short-circuited).
        var ctrl = BuildSut(current);

        await ctrl.SetAvailabilityDay(target, -2, returnUrl: null, CancellationToken.None);

        await _auditLog.DidNotReceive().LogAsync(
            AuditAction.VolunteerAvailabilitySet,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
        ctrl.TempData[TempDataKeys.SuccessMessage].Should().BeNull();
    }

    [HumansFact]
    public async Task ClearAvailabilityDay_HappyPath_CallsServiceAndAuditsAndRedirects()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        var esId = Guid.NewGuid();
        var es = new Humans.Domain.Entities.EventSettings { Id = esId };
        _shiftMgmt.GetActiveAsync().Returns(es);
        _service
            .SetDayAvailabilityAsync(target, esId, -3, false, Arg.Any<CancellationToken>())
            .Returns(true);
        var ctrl = BuildSut(current);

        var result = await ctrl.ClearAvailabilityDay(target, -3, returnUrl: null, CancellationToken.None);

        await _service.Received(1)
            .SetDayAvailabilityAsync(target, esId, -3, false, Arg.Any<CancellationToken>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.VolunteerAvailabilityCleared,
            Arg.Any<string>(), target,
            Arg.Any<string>(), current.Id,
            Arg.Any<Guid?>(), Arg.Any<string?>());
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(VolunteerTrackingController.Index));
        ctrl.TempData[TempDataKeys.SuccessMessage].Should().Be("VolTrack_Msg_AvailabilityCleared");
    }

    [HumansFact]
    public async Task ClearAvailabilityDay_NoChange_DoesNotAudit()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        var es = new Humans.Domain.Entities.EventSettings { Id = Guid.NewGuid() };
        _shiftMgmt.GetActiveAsync().Returns(es);
        // Default: SetDayAvailabilityAsync returns false → no audit row.
        var ctrl = BuildSut(current);

        await ctrl.ClearAvailabilityDay(target, -3, returnUrl: null, CancellationToken.None);

        await _auditLog.DidNotReceive().LogAsync(
            AuditAction.VolunteerAvailabilityCleared,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
        ctrl.TempData[TempDataKeys.SuccessMessage].Should().BeNull();
    }

    [HumansFact]
    public async Task SetAvailabilityDay_LocalReturnUrl_RedirectsToReturnUrl()
    {
        var current = new User { Id = Guid.NewGuid() };
        var es = new Humans.Domain.Entities.EventSettings { Id = Guid.NewGuid() };
        _shiftMgmt.GetActiveAsync().Returns(es);
        var ctrl = BuildSut(current);
        var localUrl = $"/Profile/{Guid.NewGuid()}";
        ctrl.Url.IsLocalUrl(localUrl).Returns(true);

        var result = await ctrl.SetAvailabilityDay(Guid.NewGuid(), -1, returnUrl: localUrl, CancellationToken.None);

        var redirect = Assert.IsType<LocalRedirectResult>(result);
        redirect.Url.Should().Be(localUrl);
    }

    [HumansFact]
    public async Task SetAvailabilityDay_ExternalReturnUrl_RedirectsToIndex()
    {
        var current = new User { Id = Guid.NewGuid() };
        var es = new Humans.Domain.Entities.EventSettings { Id = Guid.NewGuid() };
        _shiftMgmt.GetActiveAsync().Returns(es);
        var ctrl = BuildSut(current);
        const string externalUrl = "https://evil.test/steal";
        ctrl.Url.IsLocalUrl(externalUrl).Returns(false);

        var result = await ctrl.SetAvailabilityDay(Guid.NewGuid(), -1, returnUrl: externalUrl, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(VolunteerTrackingController.Index));
    }

    [HumansFact]
    public async Task SetAvailabilityDay_NoActiveEvent_RedirectsWithError()
    {
        var current = new User { Id = Guid.NewGuid() };
        _shiftMgmt.GetActiveAsync().Returns((Humans.Domain.Entities.EventSettings?)null);
        var ctrl = BuildSut(current);

        var result = await ctrl.SetAvailabilityDay(Guid.NewGuid(), -1, returnUrl: null, CancellationToken.None);

        await _service.DidNotReceive()
            .SetDayAvailabilityAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());
        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("VolTrack_Err_BadRequest");
    }

    private static UserInfo StubUserInfo(Guid userId, string burnerName)
    {
        var user = new User
        {
            Id = userId,
            DisplayName = burnerName,
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            GoogleEmailStatus = GoogleEmailStatus.Unknown,
        };
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = burnerName,
            FirstName = burnerName,
            LastName = "Test",
            IsApproved = true,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };
        return UserInfo.Create(
            user: user,
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: profile,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
    }
}
