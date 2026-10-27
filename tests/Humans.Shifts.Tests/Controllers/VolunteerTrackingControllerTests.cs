using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Domain;
using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Shifts.Controllers;
using Humans.Shifts.Models;
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
using Humans.Users.Contracts;
using Humans.Users.Domain;
using Humans.Users.Services;

namespace Humans.Shifts.Tests.Controllers;

/// <summary>
/// A unit-test project, so policy enforcement is checked by reflecting on the
/// <c>[Authorize(Policy = …)]</c> attributes; the policies have their own tests.
/// </summary>
public class VolunteerTrackingControllerTests
{
    private readonly UserManager<User> _userManager;
    private readonly IVolunteerTrackingManagement _service = Substitute.For<IVolunteerTrackingManagement>();
    private readonly IShiftManagement _shiftMgmt = Substitute.For<IShiftManagement>();
    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly IVolunteerTrackingExportService _exportService =
        Substitute.For<IVolunteerTrackingExportService>();
    private readonly VolunteerTrackingXlsxBuilder _xlsxBuilder = new();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IStringLocalizer<ShiftsResource> _localizer =
        Substitute.For<IStringLocalizer<ShiftsResource>>();

    public VolunteerTrackingControllerTests()
    {
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>());
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
                .Returns(new ValueTask<UserInfo?>(UserInfoFactory.Create(
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
            _service, _shiftMgmt, _burnSettings, _exportService, _xlsxBuilder,
            _userService, _localizer, NullLogger<VolunteerTrackingController>.Instance);

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

    [HumansFact]
    public void Class_RequiresShiftDashboardAccess_Policy()
    {
        var attr = typeof(VolunteerTrackingController)
            .GetCustomAttribute<AuthorizeAttribute>(inherit: false);
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
        var attr = method.GetCustomAttribute<AuthorizeAttribute>(inherit: false);
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

        var result = await ctrl.Index(false, false, false, TestContext.Current.CancellationToken);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<VolunteerTrackingPageViewModel>(view.Model);
        model.HasActiveEvent.Should().BeFalse();
        model.MainCohort.Should().BeEmpty();
        model.UnbookedCohort.Should().BeEmpty();
    }

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
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>
            {
                [aliceId] = StubUserInfo(aliceId, "Alice"),
                [bobId] = StubUserInfo(bobId, "Bob"),
                [carolId] = StubUserInfo(carolId, "Carol")
            });

        var ctrl = BuildSut(new User { Id = Guid.NewGuid() });

        var result = await ctrl.Index(false, false, false, TestContext.Current.CancellationToken);

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

        var result = await ctrl.Index(hideNoGaps: true, false, false, TestContext.Current.CancellationToken);

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

        var result = await ctrl.Index(false, hideCampSetup: true, false, TestContext.Current.CancellationToken);

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

        var result = await ctrl.Index(false, false, hideUnbookedSection: true, TestContext.Current.CancellationToken);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<VolunteerTrackingPageViewModel>(view.Model);
        model.UnbookedCohort.Should().BeEmpty();
        model.HideUnbookedSection.Should().BeTrue();
    }

    [HumansFact]
    public async Task SetCampSetup_ValidForm_RedirectsAndSetsSuccessTempData()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        _service.SetCampSetupAsync(
                target, Arg.Any<LocalDate>(), Arg.Any<string?>(),
                current.Id, Arg.Any<CancellationToken>())
            .Returns(new SetCampSetupResult(Ok: true, ErrorMessageKey: null, AutoClearedDayOffs: []));
        var ctrl = BuildSut(current);
        var form = new SetCampSetupForm { UserId = target, Date = "2026-06-14", Notes = "early" };

        var result = await ctrl.SetCampSetup(form, null, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(VolunteerTrackingController.Index));
        ctrl.TempData[TempDataKeys.SuccessMessage].Should().Be("Shifts_VolTrack_Msg_CampSetupSaved");
    }

    [HumansFact]
    public async Task SetCampSetup_ModelStateInvalid_RegexFail_RedirectsWithBadRequestError()
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

        var result = await ctrl.SetCampSetup(form, null, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(VolunteerTrackingController.Index));
        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("Shifts_VolTrack_Err_BadRequest");
        await _service.DidNotReceive().SetCampSetupAsync(
            Arg.Any<Guid>(), Arg.Any<LocalDate>(), Arg.Any<string?>(),
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
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

        var result = await ctrl.SetCampSetup(form, null, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(VolunteerTrackingController.Index));
        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("Shifts_VolTrack_Err_BadDate");
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

        var result = await ctrl.SetCampSetup(form, null, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(VolunteerTrackingController.Index));
        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("VolTrack_Err_DateOutsideWindow");
    }

    [HumansFact]
    public async Task SetCampSetup_NoCurrentUser_ReturnsForbid()
    {
        // GetCurrentUserAsync returns null when the cookie maps to a deleted/
        // inaccessible identity row even though [Authorize] passed claims.
        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns((User?)null);
        var ctrl = BuildSut(currentUser: null);
        var form = new SetCampSetupForm { UserId = Guid.NewGuid(), Date = "2026-06-14" };

        var result = await ctrl.SetCampSetup(form, null, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
    }

    [HumansFact]
    public async Task ClearCampSetup_HappyPath_CallsServiceAndRedirects()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        var ctrl = BuildSut(current);

        var result = await ctrl.ClearCampSetup(target, null, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(VolunteerTrackingController.Index));
        await _service.Received(1).ClearCampSetupAsync(
            target, current.Id, Arg.Any<CancellationToken>());
        ctrl.TempData[TempDataKeys.SuccessMessage].Should().Be("Shifts_VolTrack_Msg_CampSetupCleared");
    }

    [HumansFact]
    public async Task ClearCampSetup_EmptyUserId_DoesNotCallService()
    {
        var current = new User { Id = Guid.NewGuid() };
        var ctrl = BuildSut(current);

        var result = await ctrl.ClearCampSetup(Guid.Empty, null, TestContext.Current.CancellationToken);

        Assert.IsType<RedirectToActionResult>(result);
        await _service.DidNotReceive().ClearCampSetupAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("Shifts_VolTrack_Err_BadRequest");
    }

    [HumansFact]
    public async Task SetDayOff_HappyPath_RedirectsWithSuccess()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        _service.SetDayOffAsync(
                target, -3, "doctor", current.Id, Arg.Any<CancellationToken>())
            .Returns(new SetDayOffResult(Ok: true, ErrorMessageKey: null));
        var ctrl = BuildSut(current);
        var form = new SetDayOffForm { UserId = target, DayOffset = -3, Reason = "doctor" };

        var result = await ctrl.SetDayOff(form, null, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(VolunteerTrackingController.Index));
        ctrl.TempData[TempDataKeys.SuccessMessage].Should().Be("Shifts_VolTrack_Msg_DayOffMarked");
    }

    [HumansFact]
    public async Task SetDayOff_ServiceRejects_RedirectsWithErrorTempData()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        _service.SetDayOffAsync(
                target, -3, Arg.Any<string?>(), current.Id, Arg.Any<CancellationToken>())
            .Returns(new SetDayOffResult(Ok: false, ErrorMessageKey: "Shifts_VolTrack_Err_DayOffWithSignups"));
        var ctrl = BuildSut(current);
        var form = new SetDayOffForm { UserId = target, DayOffset = -3, Reason = null };

        var result = await ctrl.SetDayOff(form, null, TestContext.Current.CancellationToken);

        Assert.IsType<RedirectToActionResult>(result);
        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("Shifts_VolTrack_Err_DayOffWithSignups");
    }

    [HumansFact]
    public async Task ClearDayOff_HappyPath_RedirectsWithSuccess()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        _service.ClearDayOffAsync(
                target, -3, current.Id, Arg.Any<CancellationToken>())
            .Returns(new ClearDayOffResult(Removed: true));
        var ctrl = BuildSut(current);
        var form = new ClearDayOffForm { UserId = target, DayOffset = -3 };

        var result = await ctrl.ClearDayOff(form, null, TestContext.Current.CancellationToken);

        Assert.IsType<RedirectToActionResult>(result);
        ctrl.TempData[TempDataKeys.SuccessMessage].Should().Be("Shifts_VolTrack_Msg_DayOffCleared");
    }

    [HumansFact]
    public async Task ClearDayOff_NoEntryToRemove_RedirectsWithoutSuccess()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        _service.ClearDayOffAsync(
                target, -3, current.Id, Arg.Any<CancellationToken>())
            .Returns(new ClearDayOffResult(Removed: false));
        var ctrl = BuildSut(current);
        var form = new ClearDayOffForm { UserId = target, DayOffset = -3 };

        var result = await ctrl.ClearDayOff(form, null, TestContext.Current.CancellationToken);

        Assert.IsType<RedirectToActionResult>(result);
    }

    [HumansFact]
    public void SetAvailabilityDay_Requires_VolunteerTrackingWrite_Policy()
    {
        var method = typeof(VolunteerTrackingController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => string.Equals(m.Name, nameof(VolunteerTrackingController.SetAvailabilityDay), StringComparison.Ordinal));
        var attr = method.GetCustomAttribute<AuthorizeAttribute>(inherit: false);
        attr.Should().NotBeNull("SetAvailabilityDay must require VolunteerTrackingWrite");
        attr.Policy.Should().Be(PolicyNames.VolunteerTrackingWrite);
    }

    [HumansFact]
    public void ClearAvailabilityDay_Requires_VolunteerTrackingWrite_Policy()
    {
        var method = typeof(VolunteerTrackingController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => string.Equals(m.Name, nameof(VolunteerTrackingController.ClearAvailabilityDay), StringComparison.Ordinal));
        var attr = method.GetCustomAttribute<AuthorizeAttribute>(inherit: false);
        attr.Should().NotBeNull("ClearAvailabilityDay must require VolunteerTrackingWrite");
        attr.Policy.Should().Be(PolicyNames.VolunteerTrackingWrite);
    }

    [HumansTheory]
    [InlineData(true, "user")]
    [InlineData(true, "empty-user")]
    [InlineData(false, "day")]
    public async Task AvailabilityDay_RequiresMemberAndDay(bool adding, string missing)
    {
        Guid? target = string.Equals(missing, "user", StringComparison.Ordinal) ? null
            : string.Equals(missing, "empty-user", StringComparison.Ordinal) ? Guid.Empty : Guid.NewGuid();
        int? dayOffset = string.Equals(missing, "day", StringComparison.Ordinal) ? null : -2;
        var esId = Guid.NewGuid();
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(MakeBurn(esId));
        _service.SetDayAvailabilityAsync(Arg.Is(target.GetValueOrDefault()), Arg.Is(esId), dayOffset.GetValueOrDefault(), adding,
                Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var ctrl = BuildSut(new User { Id = Guid.NewGuid() });

        var result = adding
            ? await ctrl.SetAvailabilityDay(target, dayOffset, null, TestContext.Current.CancellationToken)
            : await ctrl.ClearAvailabilityDay(target, dayOffset, null, TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestResult>();
        await _service.DidNotReceive().SetDayAvailabilityAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TrackingForms_EmptyMember_DoesNotCreateTrackingRows(bool campSetup)
    {
        var actor = new User { Id = Guid.NewGuid() };
        var ctrl = BuildSut(actor);
        _service.SetCampSetupAsync(Guid.Empty, new LocalDate(2026, 7, 7), null, actor.Id, Arg.Any<CancellationToken>())
            .Returns(new SetCampSetupResult(true, null, []));
        _service.SetDayOffAsync(Guid.Empty, -2, null, actor.Id, Arg.Any<CancellationToken>())
            .Returns(new SetDayOffResult(true, null));

        var result = campSetup
            ? await ctrl.SetCampSetup(new SetCampSetupForm { Date = "2026-07-07" }, null, TestContext.Current.CancellationToken)
            : await ctrl.SetDayOff(new SetDayOffForm { DayOffset = -2 }, null, TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestResult>();
        await _service.DidNotReceive().SetCampSetupAsync(
            Arg.Any<Guid>(), Arg.Any<LocalDate>(), Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _service.DidNotReceive().SetDayOffAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task AvailabilityDay_MalformedBinding_DoesNotMutate(bool adding, bool hasActor)
    {
        var target = adding ? Guid.Empty : Guid.NewGuid();
        var dayOffset = adding ? -2 : 0;
        var esId = Guid.NewGuid();
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(MakeBurn(esId));
        _service.SetDayAvailabilityAsync(Arg.Is(target), Arg.Is(esId), dayOffset, adding, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var ctrl = BuildSut(hasActor ? new User { Id = Guid.NewGuid() } : null);
        ctrl.ModelState.AddModelError(adding ? "userId" : "dayOffset", "Malformed value");

        var result = adding
            ? await ctrl.SetAvailabilityDay(target, dayOffset, null, TestContext.Current.CancellationToken)
            : await ctrl.ClearAvailabilityDay(target, dayOffset, null, TestContext.Current.CancellationToken);

        if (hasActor) result.Should().BeOfType<BadRequestResult>();
        else result.Should().BeOfType<ForbidResult>();
        await _service.DidNotReceive().SetDayAvailabilityAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SetAvailabilityDay_HappyPath_CallsServiceAndRedirects()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        var esId = Guid.NewGuid();
        var es = MakeBurn(esId);
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(es);
        _service
            .SetDayAvailabilityAsync(target, esId, -2, true, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var ctrl = BuildSut(current);

        var result = await ctrl.SetAvailabilityDay(target, -2, returnUrl: null, TestContext.Current.CancellationToken);

        await _service.Received(1)
            .SetDayAvailabilityAsync(target, esId, -2, true, current.Id, Arg.Any<CancellationToken>());
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(VolunteerTrackingController.Index));
        ctrl.TempData[TempDataKeys.SuccessMessage].Should().Be("Shifts_VolTrack_Msg_AvailabilitySet");
    }

    [HumansFact]
    public async Task SetAvailabilityDay_NoChange_SetsNoSuccess()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        var es = MakeBurn(Guid.NewGuid());
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(es);
        // Default: SetDayAvailabilityAsync returns false → no success message (service short-circuited).
        var ctrl = BuildSut(current);

        await ctrl.SetAvailabilityDay(target, -2, returnUrl: null, TestContext.Current.CancellationToken);

        ctrl.TempData[TempDataKeys.SuccessMessage].Should().BeNull();
    }

    [HumansTheory]
    [InlineData(-3)]
    [InlineData(0)]
    public async Task ClearAvailabilityDay_HappyPath_CallsServiceAndRedirects(int dayOffset)
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        var esId = Guid.NewGuid();
        var es = MakeBurn(esId);
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(es);
        _service
            .SetDayAvailabilityAsync(target, esId, dayOffset, false, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var ctrl = BuildSut(current);

        var result = await ctrl.ClearAvailabilityDay(target, dayOffset, returnUrl: null, TestContext.Current.CancellationToken);

        await _service.Received(1)
            .SetDayAvailabilityAsync(target, esId, dayOffset, false, current.Id, Arg.Any<CancellationToken>());
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(VolunteerTrackingController.Index));
        ctrl.TempData[TempDataKeys.SuccessMessage].Should().Be("Shifts_VolTrack_Msg_AvailabilityCleared");
    }

    [HumansFact]
    public async Task ClearAvailabilityDay_NoChange_SetsNoSuccess()
    {
        var current = new User { Id = Guid.NewGuid() };
        var target = Guid.NewGuid();
        var es = MakeBurn(Guid.NewGuid());
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(es);
        // Default: SetDayAvailabilityAsync returns false → no success message.
        var ctrl = BuildSut(current);

        await ctrl.ClearAvailabilityDay(target, -3, returnUrl: null, TestContext.Current.CancellationToken);

        ctrl.TempData[TempDataKeys.SuccessMessage].Should().BeNull();
    }

    [HumansFact]
    public async Task SetAvailabilityDay_LocalReturnUrl_RedirectsToReturnUrl()
    {
        var current = new User { Id = Guid.NewGuid() };
        var es = MakeBurn(Guid.NewGuid());
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(es);
        var ctrl = BuildSut(current);
        var localUrl = $"/Profile/{Guid.NewGuid()}";
        ctrl.Url.IsLocalUrl(localUrl).Returns(true);

        var result = await ctrl.SetAvailabilityDay(Guid.NewGuid(), -1, returnUrl: localUrl, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<LocalRedirectResult>(result);
        redirect.Url.Should().Be(localUrl);
    }

    [HumansFact]
    public async Task SetAvailabilityDay_ExternalReturnUrl_RedirectsToIndex()
    {
        var current = new User { Id = Guid.NewGuid() };
        var es = MakeBurn(Guid.NewGuid());
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(es);
        var ctrl = BuildSut(current);
        const string externalUrl = "https://evil.test/steal";
        ctrl.Url.IsLocalUrl(externalUrl).Returns(false);

        var result = await ctrl.SetAvailabilityDay(Guid.NewGuid(), -1, returnUrl: externalUrl, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(VolunteerTrackingController.Index));
    }

    [HumansFact]
    public async Task SetAvailabilityDay_NoActiveEvent_RedirectsWithError()
    {
        var current = new User { Id = Guid.NewGuid() };
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns((BurnSettingsInfo?)null);
        var ctrl = BuildSut(current);

        await ctrl.SetAvailabilityDay(Guid.NewGuid(), -1, returnUrl: null, TestContext.Current.CancellationToken);

        await _service.DidNotReceive()
            .SetDayAvailabilityAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<bool>(),
                Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("Shifts_VolTrack_Err_BadRequest");
    }

    private static UserInfo StubUserInfo(Guid userId, string burnerName)
    {
        var user = new User
        {
            Id = userId,
            DisplayName = burnerName,
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
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
        return UserInfoFactory.Create(
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

    // These tests only care about the burn's Id (it is what gets forwarded to
    // SetDayAvailabilityAsync); the rest are inert defaults.
    private static BurnSettingsInfo MakeBurn(Guid id) =>
        new(
            Id: id,
            EventName: "Test Burn",
            Year: 2026,
            TimeZoneId: "Europe/Madrid",
            GateOpeningDate: new LocalDate(2026, 7, 9),
            BuildStartOffset: -7,
            EventEndOffset: 4,
            StrikeEndOffset: 6,
            FirstCrewStartOffset: -7,
            SetupWeekStartOffset: -5,
            PreEventWeekStartOffset: -3,
            FinishingWeekendStartOffset: -2,
            EarlyEntryCapacity: new Dictionary<int, int>(),
            BarriosEarlyEntryAllocation: null,
            EarlyEntryClose: null,
            IsShiftBrowsingOpen: false);
}
