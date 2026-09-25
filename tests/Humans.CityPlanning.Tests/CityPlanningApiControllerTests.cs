using System.Security.Claims;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Constants;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Humans.CityPlanning.Controllers;
using Humans.CityPlanning.Data;
using Humans.CityPlanning.Services;
using Humans.Containers.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.CityPlanning.Tests;

/// <summary>
/// Pins the map-admin gate on restore and export (health.md invariant 6) and the
/// save-then-broadcast contract (invariant 8) at the API surface the map JavaScript calls.
/// </summary>
public sealed class CityPlanningApiControllerTests : CityPlanningTestBase
{
    private readonly ICampServiceRead _campService = Substitute.For<ICampServiceRead>();
    private readonly ITeamServiceRead _teamService = Substitute.For<ITeamServiceRead>();
    private readonly IClientProxy _allClients = Substitute.For<IClientProxy>();
    private readonly CityPlanningService _service;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _campSeasonId = Guid.NewGuid();

    public CityPlanningApiControllerTests()
    {
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(2026, []));
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());

        _service = new CityPlanningService(
            new CityPlanningRepository(CityPlanningDbFactory), Clock,
            Options.Create(new CityPlanningOptions { CityPlanningTeamSlug = "city-planning" }),
            _campService, _teamService, Substitute.For<IUserServiceRead>(), Substitute.For<IAuditLogService>());
    }

    private CityPlanningApiController CreateController(params string[] roles)
    {
        var hubClients = Substitute.For<IHubClients>();
        hubClients.All.Returns(_allClients);
        var hubContext = Substitute.For<IHubContext<CityPlanningHub>>();
        hubContext.Clients.Returns(hubClients);

        var userManager = Substitute.For<UserManager<User>>(
            Substitute.For<IUserStore<User>>(), null, null, null, null, null, null, null, null);
        userManager.GetUserId(Arg.Any<ClaimsPrincipal>()).Returns(_userId.ToString());

        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r))
            .Append(new Claim(ClaimTypes.NameIdentifier, _userId.ToString()));
        var controller = new CityPlanningApiController(
            _service, _campService, Substitute.For<IContainerService>(),
            Substitute.For<IAuthorizationService>(), hubContext, userManager,
            NullLogger<CityPlanningApiController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
                },
            },
        };
        return controller;
    }

    private const string Square = """{"type":"Polygon","coordinates":[[[0,0],[0,1],[1,1],[0,0]]]}""";

    [HumansFact]
    public async Task RestoreCampPolygon_UserWhoIsNotMapAdmin_IsForbiddenAndWritesNothing()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _service.SaveCampPolygonAsync(_campSeasonId, Square, 10, Guid.NewGuid(), cancellationToken: ct);
        var historyId = (await CityPlanningDb.CampPolygonHistories.SingleAsync(ct)).Id;

        var result = await CreateController().RestoreCampPolygon(_campSeasonId, historyId, ct);

        result.Should().BeOfType<ForbidResult>();
        (await CityPlanningDb.CampPolygonHistories.CountAsync(ct)).Should().Be(1);
    }

    [HumansFact]
    public async Task ExportGeoJson_UserWhoIsNotMapAdmin_IsForbidden()
    {
        var result = await CreateController().ExportGeoJson(null, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ForbidResult>();
    }

    [HumansFact]
    public async Task SaveCampPolygon_BroadcastsTheSavedShapeToEveryConnectedMap()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        var result = await CreateController(RoleNames.CampAdmin).SaveCampPolygon(
            _campSeasonId, new SaveCampPolygonRequest(Square, 10), ct);

        result.Should().BeOfType<OkObjectResult>();
        await _allClients.Received(1).SendCoreAsync(
            "CampPolygonUpdated",
            Arg.Is<object?[]>(a => (Guid)a[0]! == _campSeasonId && (string)a[1]! == Square),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SaveCampPolygon_BroadcastFails_TheSaveStandsAndTheCallerGetsOk()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _allClients.SendCoreAsync(default!, default!, default)
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("hub down"));

        var result = await CreateController(RoleNames.CampAdmin).SaveCampPolygon(
            _campSeasonId, new SaveCampPolygonRequest(Square, 10), ct);

        result.Should().BeOfType<OkObjectResult>();
        (await CityPlanningDb.CampPolygons.SingleAsync(ct)).GeoJson.Should().Be(Square);
    }
}
