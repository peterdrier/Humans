using Humans.Containers;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authorization;
using Humans.Base.Extensions;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Humans.CityPlanning.Controllers;
using Humans.CityPlanning.Data;
using Humans.CityPlanning.Services;
using Humans.Containers.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NodaTime;
using Xunit;

namespace Humans.CityPlanning.Tests;

/// <summary>
/// Pins the map-admin gate on restore and export, team members included (health.md
/// invariant 6), and the save-then-broadcast contract (invariant 8) at the API surface the map JavaScript calls.
/// </summary>
public sealed class CityPlanningApiControllerTests : CityPlanningTestBase
{
    private readonly IContainerService _containers = Substitute.For<IContainerService>();
    private IStringLocalizer<ContainersResource> _containersLocalizer = Substitute.For<IStringLocalizer<ContainersResource>>();
    private IAuthorizationService _authorization;
    private readonly ICampServiceRead _campService = Substitute.For<ICampServiceRead>();
    private readonly ITeamServiceRead _teamService = Substitute.For<ITeamServiceRead>();
    private readonly ILogger<CityPlanningApiController> _logger = Substitute.For<ILogger<CityPlanningApiController>>();
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
        _authorization = MapAdminAuthorization(_service);
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
            _service, _campService, _containers, _containersLocalizer,
            _authorization, hubContext, userManager,
            _logger)
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

    [HumansTheory]
    [InlineData("en", "Invalid container placement GeoJSON.")]
    [InlineData("es", "El GeoJSON de ubicación del contenedor no es válido.")]
    [InlineData("de", "Ungültiges GeoJSON für die Containerplatzierung.")]
    [InlineData("it", "GeoJSON di posizionamento del container non valido.")]
    [InlineData("fr", "Le GeoJSON de placement du conteneur est invalide.")]
    [InlineData("ca", "El GeoJSON d’ubicació del contenidor no és vàlid.")]
    public async Task InvalidContainerPlacement_ReturnsTheOwnersLocalizedError(string culture, string error)
    {
        using var cultureScope = new CultureScope(culture);
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        _containersLocalizer = services.GetRequiredService<IStringLocalizer<ContainersResource>>();
        _authorization = Substitute.For<IAuthorizationService>();
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        var id = Guid.NewGuid();
        _containers.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(
            new ContainerDto(id, Guid.NewGuid(), "Container", null, [], Instant.MinValue, Instant.MinValue));
        _containers.SavePlacementAsync(id, 2026, "{}", _userId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(error));

        var result = await CreateController().SaveContainerPlacement(
            id, 2026, new SaveContainerPlacementRequest("{}"), TestContext.Current.CancellationToken);

        result.Should().BeOfType<UnprocessableEntityObjectResult>().Which.Value.Should().Be(error);
    }

    [HumansFact]
    public async Task SaveContainerPlacement_DoesNotConvertUnexpectedFailuresIntoValidationErrors()
    {
        _authorization = Substitute.For<IAuthorizationService>();
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        var id = Guid.NewGuid();
        _containers.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(
            new ContainerDto(id, Guid.NewGuid(), "Container", null, [], Instant.MinValue, Instant.MinValue));
        _containers.SavePlacementAsync(id, 2026, "{}", _userId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Database unavailable"));

        var act = () => CreateController().SaveContainerPlacement(
            id, 2026, new SaveContainerPlacementRequest("{}"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Database unavailable");
    }

    [HumansTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ContainerPlacementMutations_AuthorizeRouteYearAndDoNotWriteWhenDenied(int operation)
    {
        var id = Guid.NewGuid();
        const int year = 2025;
        _containers.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(
            new ContainerDto(id, Guid.NewGuid(), "Container", null, [], Instant.MinValue, Instant.MinValue));
        _authorization = Substitute.For<IAuthorizationService>();
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(call => call.ArgAt<object>(1) is ContainerAuthorizationTarget { Year: year }
                ? AuthorizationResult.Failed() : AuthorizationResult.Success());
        var controller = CreateController();
        var ct = TestContext.Current.CancellationToken;

        var result = operation switch
        {
            0 => await controller.SaveContainerPlacement(id, year, new SaveContainerPlacementRequest("{}"), ct),
            1 => await controller.UpdateContainerPlacementNotes(id, year, new UpdateContainerPlacementNotesRequest(), ct),
            _ => await controller.ClearContainerPlacement(id, year, ct),
        };

        result.Should().BeOfType<ForbidResult>();
        _containers.ReceivedCalls().Should().ContainSingle();
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
    public async Task RestoreCampPolygon_CityPlanningTeamMemberWithNoRole_Restores()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var teamId = Guid.NewGuid();
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>
        {
            [teamId] = new(
                teamId, "City Planning", null, "city-planning",
                IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
                RequiresApproval: false, IsPublicPage: false, IsHidden: false,
                IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
                Members: [new(Guid.NewGuid(), _userId, string.Empty, null, null, TeamMemberRole.Member, Instant.MinValue)]),
        });
        await _service.SaveCampPolygonAsync(_campSeasonId, Square, 10, Guid.NewGuid(), cancellationToken: ct);
        var historyId = (await CityPlanningDb.CampPolygonHistories.SingleAsync(ct)).Id;

        var result = await CreateController().RestoreCampPolygon(_campSeasonId, historyId, ct);

        result.Should().BeOfType<OkObjectResult>();
        (await CityPlanningDb.CampPolygonHistories.CountAsync(ct)).Should().Be(2);
    }

    [HumansFact]
    public async Task RestoreCampPolygon_HistoryIdFromAnotherSeason_IsNotFoundAndWritesNothing()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>
        {
            [Guid.NewGuid()] = new(
                Guid.NewGuid(), "City Planning", null, "city-planning",
                IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
                RequiresApproval: false, IsPublicPage: false, IsHidden: false,
                IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
                Members: [new(Guid.NewGuid(), _userId, string.Empty, null, null, TeamMemberRole.Member, Instant.MinValue)]),
        });
        await _service.SaveCampPolygonAsync(Guid.NewGuid(), Square, 10, Guid.NewGuid(), cancellationToken: ct);
        var otherSeasonHistoryId = (await CityPlanningDb.CampPolygonHistories.SingleAsync(ct)).Id;

        var result = await CreateController().RestoreCampPolygon(_campSeasonId, otherSeasonHistoryId, ct);

        result.Should().BeOfType<NotFoundResult>();
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

    [HumansTheory]
    [InlineData("not json")]
    [InlineData("""{"type":"Point","coordinates":[0,0]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[]]}""")]
    public async Task SaveCampPolygon_InvalidGeoJson_ReturnsBadRequestWithoutSaving(string geoJson)
    {
        var result = await CreateController(RoleNames.CampAdmin).SaveCampPolygon(
            _campSeasonId, new SaveCampPolygonRequest(geoJson, 10),
            Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestResult>();
        (await CityPlanningDb.CampPolygons.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
        (await CityPlanningDb.CampPolygonHistories.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
        _allClients.ReceivedCalls().Should().BeEmpty();
        var warning = _logger.ReceivedCalls().Single(c => string.Equals(c.GetMethodInfo().Name, "Log", StringComparison.Ordinal));
        warning.GetArguments()[0].Should().Be(LogLevel.Warning);
        warning.GetArguments()[3].Should().BeNull();
        warning.GetArguments()[2]!.ToString().Should().Contain(_campSeasonId.ToString()).And.Contain("Invalid GeoJSON.");
    }

    [HumansFact]
    public async Task SaveCampPolygon_NegativeArea_ReturnsBadRequestWithoutSavingOrBroadcasting()
    {
        var result = await CreateController(RoleNames.CampAdmin).SaveCampPolygon(
            _campSeasonId, new SaveCampPolygonRequest(Square, -1),
            Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestResult>();
        (await CityPlanningDb.CampPolygons.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
        (await CityPlanningDb.CampPolygonHistories.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
        _allClients.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task RestoreCampPolygon_NegativeHistoricArea_ReturnsBadRequestWithoutWritingOrBroadcasting()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var history = new Humans.CityPlanning.Domain.CampPolygonHistory
        {
            CampSeasonId = _campSeasonId,
            GeoJson = Square,
            AreaSqm = -1,
            ModifiedByUserId = _userId,
            ModifiedAt = Clock.GetCurrentInstant(),
        };
        CityPlanningDb.CampPolygonHistories.Add(history);
        await CityPlanningDb.SaveChangesAsync(ct);

        var result = await CreateController(RoleNames.Admin).RestoreCampPolygon(_campSeasonId, history.Id, ct);

        result.Should().BeOfType<BadRequestResult>();
        (await CityPlanningDb.CampPolygons.CountAsync(ct)).Should().Be(0);
        (await CityPlanningDb.CampPolygonHistories.CountAsync(ct)).Should().Be(1);
        _allClients.ReceivedCalls().Should().BeEmpty();
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

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task PolygonWrite_BroadcastPreparationFailure_ReturnsSavedResult(bool restore)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        Guid historyId = default;
        if (restore)
        {
            await _service.SaveCampPolygonAsync(_campSeasonId, Square, 10, _userId, cancellationToken: ct);
            historyId = (await CityPlanningDb.CampPolygonHistories.SingleAsync(ct)).Id;
        }
        _campService.GetCampSeasonByIdAsync(_campSeasonId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("Camp directory unavailable"));
        var controller = CreateController(RoleNames.CampAdmin);

        var result = restore
            ? await controller.RestoreCampPolygon(_campSeasonId, historyId, ct)
            : await controller.SaveCampPolygon(_campSeasonId, new SaveCampPolygonRequest(Square, 10), ct);

        result.Should().BeOfType<OkObjectResult>();
        (await CityPlanningDb.CampPolygons.SingleAsync(ct)).GeoJson.Should().Be(Square);
        (await CityPlanningDb.CampPolygonHistories.CountAsync(ct)).Should().Be(restore ? 2 : 1);
        await _allClients.DidNotReceive().SendCoreAsync(
            Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SaveCampPolygon_CancelledBroadcast_PropagatesCancellationAfterSave()
    {
        using var cancellation = new CancellationTokenSource();
        _allClients.SendCoreAsync(default!, default!, cancellation.Token)
            .ReturnsForAnyArgs(_ => CancelBroadcastAsync(cancellation));

        var act = () => CreateController(RoleNames.CampAdmin).SaveCampPolygon(
            _campSeasonId, new SaveCampPolygonRequest(Square, 10), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await CityPlanningDb.CampPolygons.SingleAsync(Xunit.TestContext.Current.CancellationToken)).GeoJson.Should().Be(Square);
    }

    private static async Task CancelBroadcastAsync(CancellationTokenSource cancellation)
    {
        await cancellation.CancelAsync();
        throw new OperationCanceledException(cancellation.Token);
    }
}
