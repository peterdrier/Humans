using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Governance.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Controllers;
using Humans.Shifts.Domain;
using Humans.Shifts.Helpers;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Domain;
using Humans.Users.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Shifts.Tests.Controllers;

/// <summary>
/// A department coordinator may delete only rotas and shifts of the department in the URL.
/// The id in the route is untrusted: one that belongs to another team is a 404, and the
/// service is never asked to delete it. Edits validate the posted model before writing,
/// and invalid edits retain their attempted values and field errors.
/// </summary>
public class ShiftAdminControllerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid TeamId = Guid.NewGuid();
    private static readonly Guid OtherTeamId = Guid.NewGuid();
    private const string Slug = "gate";

    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly ITeamServiceRead _teamService = Substitute.For<ITeamServiceRead>();
    private readonly IUserServiceRead _userService = Substitute.For<IUserServiceRead>();
    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly IShiftSignupService _signupService = Substitute.For<IShiftSignupService>();
    private readonly IShiftRowView _shiftView = Substitute.For<IShiftRowView>();
    private readonly IVolunteerTrackingService _tracking = Substitute.For<IVolunteerTrackingService>();

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

    public ShiftAdminControllerTests()
    {
        _userService.GetUserInfoAsync(UserId, Arg.Any<CancellationToken>()).Returns(MakeUserInfo(UserId));
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(
            new Dictionary<Guid, TeamInfo> { [TeamId] = MakeTeam(TeamId, Slug) });
        // The caller coordinates the URL's department and nothing else.
        _shiftMgmt.IsDeptCoordinatorAsync(UserId, TeamId).Returns(true);
        _shiftMgmt.IsDeptCoordinatorAsync(UserId, OtherTeamId).Returns(false);
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(Event);
        _teamService.GetTeamAsync(TeamId, Arg.Any<CancellationToken>()).Returns(MakeTeam(TeamId, Slug));
        _shiftMgmt.GetTagsAsync().Returns([]);
        _shiftMgmt.GetStaffingSnapshotAsync(Event.Id, TeamId).Returns(ShiftStaffingSnapshot.Empty);
        _shiftMgmt.GetRotasByDepartmentAsync(TeamId, Event.Id).Returns([]);
    }

    [HumansFact]
    public async Task DeleteRota_OtherTeamsRota_IsNotFound_AndNotDeleted()
    {
        var rota = MakeRota(OtherTeamId);
        _shiftMgmt.GetRotaByIdAsync(rota.Id).Returns(rota);

        var result = await BuildSut().DeleteRota(Slug, rota.Id);

        result.Should().BeOfType<NotFoundResult>();
        await _shiftMgmt.DidNotReceive().DeleteRotaAsync(Arg.Any<Guid>());
    }

    [HumansFact]
    public async Task DeleteRota_OwnTeamsRota_Deletes()
    {
        var rota = MakeRota(TeamId);
        _shiftMgmt.GetRotaByIdAsync(rota.Id).Returns(rota);

        var result = await BuildSut().DeleteRota(Slug, rota.Id);

        result.Should().BeOfType<RedirectToActionResult>();
        await _shiftMgmt.Received(1).DeleteRotaAsync(rota.Id);
    }

    [HumansFact]
    public async Task DeleteShift_OtherTeamsShift_IsNotFound_AndNotDeleted()
    {
        var shift = MakeShift(MakeRota(OtherTeamId));
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(shift);

        var result = await BuildSut().DeleteShift(Slug, shift.Id);

        result.Should().BeOfType<NotFoundResult>();
        await _shiftMgmt.DidNotReceive().DeleteShiftAsync(Arg.Any<Guid>());
    }

    [HumansFact]
    public async Task DeleteShift_OwnTeamsShift_Deletes()
    {
        var shift = MakeShift(MakeRota(TeamId));
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(shift);

        var result = await BuildSut().DeleteShift(Slug, shift.Id);

        result.Should().BeOfType<RedirectToActionResult>();
        await _shiftMgmt.Received(1).DeleteShiftAsync(shift.Id);
    }

    [HumansFact]
    public async Task EditRota_InvalidModel_RendersAttemptedEditWithoutChangingRota()
    {
        var rota = MakeRota(TeamId);
        _shiftMgmt.GetRotaByIdAsync(rota.Id).Returns(rota);
        _shiftMgmt.GetRotasByDepartmentAsync(TeamId, Event.Id).Returns([rota]);
        var ctrl = BuildSut();
        ctrl.ModelState.SetModelValue(nameof(EditRotaModel.Name), "", "");
        ctrl.ModelState.AddModelError(nameof(EditRotaModel.Name), "required");
        var posted = new EditRotaModel { Name = "", Description = "attempted description", TagIds = Guid.NewGuid().ToString(), Priority = ShiftPriority.Essential };

        var result = await ctrl.EditRota(Slug, rota.Id, posted);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be("Index");
        view.ViewData["InvalidRotaEdit"].Should().BeSameAs(posted);
        posted.RotaId.Should().Be(rota.Id);
        view.ViewData.ModelState[nameof(EditRotaModel.Name)]!.Errors.Should().ContainSingle();
        var page = view.Model.Should().BeOfType<ShiftAdminViewModel>().Subject;
        page.Rotas.Should().ContainSingle().Which.Should().BeSameAs(rota);
        page.CanManageShifts.Should().BeTrue();
        rota.Name.Should().Be("Rota");
        rota.Description.Should().BeNull();
        await _shiftMgmt.DidNotReceive().UpdateRotaAsync(Arg.Any<Rota>(), Arg.Any<IReadOnlyList<Guid>?>());
    }

    [HumansFact]
    public async Task EditShift_InvalidNumber_RendersRawAttemptAndPageDataWithoutSaving()
    {
        var rota = MakeRota(TeamId);
        var shift = MakeShift(rota);
        rota.Shifts.Add(shift);
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(shift);
        _shiftMgmt.GetRotasByDepartmentAsync(TeamId, Event.Id).Returns([rota]);
        var ctrl = BuildSut();
        ctrl.ModelState.SetModelValue(nameof(EditShiftModel.DurationHours), "not a number", "not a number");
        ctrl.ModelState.AddModelError(nameof(EditShiftModel.DurationHours), "Invalid number");
        var posted = new EditShiftModel { StartTime = "09:30", Description = "attempted description", AdminOnly = true };

        var result = await ctrl.EditShift(Slug, shift.Id, posted);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be("Index");
        view.ViewData["InvalidShiftEdit"].Should().BeSameAs(posted);
        posted.ShiftId.Should().Be(shift.Id);
        view.ViewData.ModelState[nameof(EditShiftModel.DurationHours)]!.AttemptedValue.Should().Be("not a number");
        view.ViewData.ModelState[nameof(EditShiftModel.DurationHours)]!.Errors.Should().ContainSingle();
        view.Model.Should().BeOfType<ShiftAdminViewModel>().Subject.Rotas.Should().ContainSingle();
        shift.StartTime.Should().Be(new LocalTime(8, 0));
        shift.Description.Should().BeNull();
        shift.AdminOnly.Should().BeFalse();
        await _shiftMgmt.DidNotReceive().UpdateShiftAsync(Arg.Any<UpdateShiftInput>());
    }

    [HumansFact]
    public async Task EditShift_InvalidStartTime_RendersFieldErrorWithoutSaving()
    {
        var shift = MakeShift(MakeRota(TeamId));
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(shift);
        var ctrl = BuildSut();
        var result = await ctrl.EditShift(Slug, shift.Id, new EditShiftModel { StartTime = "bad time" });

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be("Index");
        view.ViewData.ModelState[nameof(EditShiftModel.StartTime)]!.Errors.Should().ContainSingle();
        await _shiftMgmt.DidNotReceive().UpdateShiftAsync(Arg.Any<UpdateShiftInput>());
    }

    [HumansFact]
    public async Task EditShift_OtherTeamsShift_IsNotFoundWithoutSaving()
    {
        var shift = MakeShift(MakeRota(OtherTeamId));
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(shift);
        var result = await BuildSut().EditShift(Slug, shift.Id, new EditShiftModel { StartTime = "08:00" });

        result.Should().BeOfType<NotFoundResult>();
        await _shiftMgmt.DidNotReceive().UpdateShiftAsync(Arg.Any<UpdateShiftInput>());
    }

    private ShiftAdminController BuildSut()
    {
        var ctrl = new ShiftAdminController(
            _teamService,
            _shiftMgmt,
            _burnSettings,
            _signupService,
            _shiftView,
            _userService,
            Substitute.For<IAuthorizationService>(),
            Substitute.For<IClock>(),
            new ShiftAdminPageBuilder(_shiftMgmt, Substitute.For<IMembershipCalculatorRead>(), _userService, _teamService),
            new ShiftVolunteerSearchBuilder(_burnSettings, _userService, _shiftView, _signupService, _tracking),
            Substitute.For<IRotaCoordinatorMessageService>(),
            NullLogger<ShiftAdminController>.Instance);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], "test")),
        };
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        http.RequestServices = services.BuildServiceProvider();
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        ctrl.Url = Substitute.For<IUrlHelper>();
        return ctrl;
    }

    private static Rota MakeRota(Guid teamId) => new()
    {
        Id = Guid.NewGuid(),
        TeamId = teamId,
        EventSettingsId = Guid.NewGuid(),
        Name = "Rota",
        Policy = SignupPolicy.Public,
        Period = RotaPeriod.Event,
    };

    private static Shift MakeShift(Rota rota) => new()
    {
        Id = Guid.NewGuid(),
        RotaId = rota.Id,
        Rota = rota,
        DayOffset = 0,
        StartTime = new LocalTime(8, 0),
        Duration = Duration.FromHours(4),
        MaxVolunteers = 5,
    };

    private static TeamInfo MakeTeam(Guid id, string slug) => new(
        id, "Gate", null, slug,
        IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
        RequiresApproval: false, IsPublicPage: false, IsHidden: false,
        IsPromotedToDirectory: false, CreatedAt: Instant.MinValue, Members: []);

    private static UserInfo MakeUserInfo(Guid userId) => UserInfoFactory.Create(
        user: new User { Id = userId, DisplayName = "Coord", PreferredLanguage = "en" },
        userEmails: [],
        eventParticipations: [],
        externalLogins: [],
        profile: new Profile { UserId = userId, BurnerName = "Coord" },
        contactFields: [],
        profileLanguages: [],
        volunteerHistory: [],
        communicationPreferences: []);
}
