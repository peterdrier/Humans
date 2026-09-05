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
/// service is never asked to delete it.
/// </summary>
public class ShiftAdminControllerDeleteScopeTests
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

    public ShiftAdminControllerDeleteScopeTests()
    {
        _userService.GetUserInfoAsync(UserId, Arg.Any<CancellationToken>()).Returns(MakeUserInfo(UserId));
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(
            new Dictionary<Guid, TeamInfo> { [TeamId] = MakeTeam(TeamId, Slug) });
        // The caller coordinates the URL's department and nothing else.
        _shiftMgmt.IsDeptCoordinatorAsync(UserId, TeamId).Returns(true);
        _shiftMgmt.IsDeptCoordinatorAsync(UserId, OtherTeamId).Returns(false);
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
