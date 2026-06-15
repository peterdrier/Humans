using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.DTOs.Shifts;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Humans.Web.Models;
using Humans.Web.Models.Shifts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Tests for the Shift Summary by Camp actions on <see cref="ShiftsController"/>:
/// the coordinator/manager authorization gate, the no-active-event short-circuit,
/// the invalid-scope 404, and the controller's display sort (Table 2 by hours
/// descending with the campless row last).
/// </summary>
public class ShiftsControllerSummaryTests
{
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IShiftSignupService _signupService = Substitute.For<IShiftSignupService>();
    private readonly IVolunteerTrackingService _volunteerTrackingService = Substitute.For<IVolunteerTrackingService>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IStringLocalizer<SharedResource> _localizer = Substitute.For<IStringLocalizer<SharedResource>>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ShiftBrowsePageBuilder _builder;
    private readonly ILogger<ShiftsController> _logger = NullLogger<ShiftsController>.Instance;

    private static readonly EventSettings ActiveEvent = new()
    {
        Id = Guid.NewGuid(),
        EventName = "Test Event",
        GateOpeningDate = new LocalDate(2026, 7, 1),
        TimeZoneId = "UTC",
        IsActive = true
    };

    public ShiftsControllerSummaryTests()
    {
        _localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        _builder = new ShiftBrowsePageBuilder(_shiftMgmt, _teamService);
    }

    // Authorization is declarative: [Authorize(Policy = ShiftDepartmentManager)] on
    // each Summary action (enforced by the auth middleware / the policy's own tests),
    // so it isn't re-tested here by calling the action method directly.

    [HumansFact]
    public async Task Summary_NoActiveEvent_ReturnsNoActiveEventView()
    {
        var userId = Guid.NewGuid();
        var ctrl = BuildSut(userId);
        _shiftMgmt.GetActiveAsync().Returns((EventSettings?)null);

        var result = await ctrl.Summary();

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be("NoActiveEvent");
    }

    [HumansFact]
    public async Task Summary_InvalidScope_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        var ctrl = BuildSut(userId);
        _shiftMgmt.GetActiveAsync().Returns(ActiveEvent);
        _shiftMgmt.BuildSummaryAsync(ActiveEvent, "ghost", null, Arg.Any<CancellationToken>())
            .Returns((ShiftSummary?)null);

        var result = await ctrl.SummaryTeam("ghost");

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Summary_OrdersCampsByHoursDescendingWithCamplessLast()
    {
        var userId = Guid.NewGuid();
        var ctrl = BuildSut(userId);
        _shiftMgmt.GetActiveAsync().Returns(ActiveEvent);

        var campX = Guid.NewGuid();
        var campY = Guid.NewGuid();
        var campZ = Guid.NewGuid();
        var summary = new ShiftSummary(
            ShiftSummaryScope.Global, null, null, null, null,
            Humans:
            [
                new ShiftSummaryHumanRow(Guid.NewGuid(), "Low", campX, "X", 5, 1),
                new ShiftSummaryHumanRow(Guid.NewGuid(), "High", campY, "Y", 10, 1),
            ],
            Camps:
            [
                new ShiftSummaryCampRow(campX, "X", 1, 5, 1),
                new ShiftSummaryCampRow(null, null, 1, 100, 1), // campless — most hours, must sort last
                new ShiftSummaryCampRow(campY, "Y", 1, 10, 1),
                new ShiftSummaryCampRow(campZ, "Z", 0, 0, 0),
            ],
            TeamLinks: [],
            RotaLinks: []);
        _shiftMgmt.BuildSummaryAsync(ActiveEvent, null, null, Arg.Any<CancellationToken>())
            .Returns(summary);

        var result = await ctrl.Summary();

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be("Summary");
        var model = view.Model.Should().BeOfType<ShiftSummaryViewModel>().Subject;

        // Real camps by hours desc (Y, X, Z), campless last regardless of its hours.
        model.Camps.Select(c => c.CampId).Should().Equal(campY, campX, campZ, null);
        // Table 1 by hours desc.
        model.Humans.Select(h => h.Name).Should().Equal("High", "Low");
    }

    private ShiftsController BuildSut(Guid userId)
    {
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(MakeUserInfo(userId));
        var ctrl = new ShiftsController(
            _shiftMgmt, _signupService, _volunteerTrackingService, _shiftView, _teamService,
            _auditLogService, _userService, _localizer, _clock, _builder, _logger);
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
        ctrl.Url = Substitute.For<IUrlHelper>();
        return ctrl;
    }

    private static UserInfo MakeUserInfo(Guid userId) =>
        UserInfo.Create(
            user: new User
            {
                Id = userId,
                DisplayName = "Coordinator",
                PreferredLanguage = "en",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: new Profile
            {
                UserId = userId,
                BurnerName = "Coordinator",
                FirstName = "Co",
                LastName = "Ordinator",
                State = ProfileState.Active,
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            },
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
}
